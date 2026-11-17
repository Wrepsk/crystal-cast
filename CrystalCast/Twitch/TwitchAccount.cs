using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace CrystalCast.Twitch;

internal enum TwitchAccountState
{
    SignedOut,
    /// <summary>Asking Twitch for a login code.</summary>
    Starting,
    /// <summary>Waiting for the user to approve CrystalCast on Twitch's activate page.</summary>
    WaitingForApproval,
    SignedIn,
}

internal sealed record TwitchDeviceCode(string DeviceCode, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, int IntervalSeconds);

internal sealed record TwitchStream(
    string UserId,
    string Login,
    string DisplayName,
    string GameName,
    string Title,
    int ViewerCount,
    DateTimeOffset StartedAt,
    string ThumbnailUrl);

/// <summary>Whether Twitch sent a chat message, why not, and the id it gave the message.</summary>
internal readonly record struct TwitchSendResult(bool Sent, string Error, string MessageId = "");

/// <summary>
/// The user's Twitch account, logged in with Twitch's device code login: the plugin gets a code, the user
/// approves it on twitch.tv/activate, and Twitch hands the plugin a token. No password passes through the
/// plugin. The token is used for the followed-channels list and sending chat messages, and is checked with
/// Twitch every hour as Twitch asks apps to.
/// <para>
/// Network calls run in the background. The tab reads the state each frame; nothing here touches game state.
/// </para>
/// </summary>
internal sealed class TwitchAccount : IDisposable
{
    internal const string Scopes = "user:read:follows user:write:chat user:read:emotes chat:read";

    /// <summary>Lets chat connect as the user, so they show in the channel's list of who's in chat.</summary>
    internal const string ChatReadScope = "chat:read";
    private const string IdBase = "https://id.twitch.tv/oauth2";
    private const string ApiBase = "https://api.twitch.tv/helix";
    private static readonly TimeSpan ValidateInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly TwitchTokenStore store;
    private readonly IPluginLog? log;
    private readonly string clientId;
    private readonly HttpClient http;
    private readonly CancellationTokenSource disposal = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> userIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private TwitchTokens? tokens;

    /// <summary>
    /// What the current token may do, as Twitch reported when it was last checked. Empty until then, so a login
    /// from an older version (with fewer permissions) is only trusted with what it has.
    /// </summary>
    private volatile string[] grantedScopes = [];
    private TwitchDeviceCode? pendingCode;
    private CancellationTokenSource? signInCancel;
    private volatile TwitchAccountState state;
    private volatile string lastError = string.Empty;
    private readonly Task validateLoop;

    public TwitchAccount(TwitchTokenStore store, IPluginLog? log, string clientId, HttpMessageHandler? handler = null)
    {
        this.store = store;
        this.log = log;
        this.clientId = clientId;
        http = new HttpClient(handler ?? new SocketsHttpHandler())
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CrystalCast", typeof(TwitchAccount).Assembly.GetName().Version?.ToString(3) ?? "1"));

        if (Configured && store.Load() is { } saved)
        {
            tokens = saved;
            state = TwitchAccountState.SignedIn;
        }

        validateLoop = Task.Run(ValidateLoopAsync);
    }

    /// <summary>False in builds without a Twitch app client ID; login is then unavailable.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(clientId);

    public TwitchAccountState State => state;
    public string LastError => lastError;
    public TwitchDeviceCode? PendingCode => pendingCode;
    public string Login => tokens?.Login ?? string.Empty;
    public string DisplayName => tokens?.DisplayName is { Length: > 0 } name ? name : Login;
    public string UserId => tokens?.UserId ?? string.Empty;

    /// <summary>
    /// True when Twitch has reported the login's permissions and some that CrystalCast now asks for are missing:
    /// a login from an older version. Logging in again grants them.
    /// </summary>
    public bool NeedsNewLogin => state == TwitchAccountState.SignedIn && MissingScopes(grantedScopes).Count > 0;

    /// <summary>The permissions CrystalCast asks for that <paramref name="granted"/> lacks; none until Twitch has answered.</summary>
    internal static IReadOnlyList<string> MissingScopes(IReadOnlyCollection<string> granted) =>
        granted.Count == 0 ? [] : Scopes.Split(' ').Where(scope => !granted.Contains(scope)).ToArray();

    /// <summary>Whether the current login has <paramref name="scope"/>, as Twitch last reported.</summary>
    public bool HasScope(string scope) => state == TwitchAccountState.SignedIn && grantedScopes.Contains(scope, StringComparer.Ordinal);

    /// <summary>
    /// Who chat connects as: the user's login when their token may read chat, otherwise empty for a guest
    /// connection. A change makes the chat reconnect.
    /// </summary>
    public string ChatIdentity => HasScope(ChatReadScope) ? Login : string.Empty;

    /// <summary>The login and a current token for connecting chat as the user, or null to connect as a guest.</summary>
    public async Task<TwitchChatLogin?> GetChatLoginAsync(CancellationToken cancellationToken)
    {
        var login = Login;
        if (!HasScope(ChatReadScope) || string.IsNullOrEmpty(login))
            return null;

        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(token) ? null : new TwitchChatLogin(login, token);
    }

    /// <summary>False when the login couldn't be stored encrypted, so it ends when the game closes.</summary>
    public bool StaysLoggedIn => store.Persistent;

    /// <summary>Asks Twitch for a login code; <see cref="PendingCode"/> holds it once it arrives.</summary>
    public void BeginSignIn()
    {
        if (!Configured || state is TwitchAccountState.Starting or TwitchAccountState.WaitingForApproval)
            return;

        lastError = string.Empty;
        state = TwitchAccountState.Starting;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(disposal.Token);
        Interlocked.Exchange(ref signInCancel, cancel)?.Cancel();
        _ = Task.Run(() => SignInAsync(cancel.Token));
    }

    public void CancelSignIn()
    {
        Interlocked.Exchange(ref signInCancel, null)?.Cancel();
        pendingCode = null;
        if (state is TwitchAccountState.Starting or TwitchAccountState.WaitingForApproval)
            state = TwitchAccountState.SignedOut;
    }

    /// <summary>Forgets the login here and tells Twitch to revoke the token.</summary>
    public void SignOut()
    {
        CancelSignIn();
        TwitchTokens? old;
        lock (sync)
        {
            old = tokens;
            tokens = null;
        }

        store.Delete();
        state = TwitchAccountState.SignedOut;
        lastError = string.Empty;
        if (old != null)
            _ = Task.Run(() => RevokeAsync(old.AccessToken));
    }

    public void Dispose()
    {
        disposal.Cancel();
        try
        {
            validateLoop.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        http.Dispose();
        disposal.Dispose();
        refreshGate.Dispose();
    }

    // ---------------------------------------------------------------- Helix

    /// <summary>Channels the user follows that are live now, most viewers first.</summary>
    public async Task<IReadOnlyList<TwitchStream>> GetFollowedLiveAsync(CancellationToken cancellationToken)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId))
            return [];

        using var response = await SendApiAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/streams/followed?user_id={Uri.EscapeDataString(userId)}&first=100"),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<HelixPage<HelixStream>>(cancellationToken).ConfigureAwait(false);
        return ToStreams(page?.Data);
    }

    internal static IReadOnlyList<TwitchStream> ToStreams(IEnumerable<HelixStream>? streams)
    {
        return (streams ?? [])
            .Where(stream => !string.IsNullOrWhiteSpace(stream.UserLogin))
            .Select(stream => new TwitchStream(
                stream.UserId ?? string.Empty,
                stream.UserLogin!.ToLowerInvariant(),
                string.IsNullOrWhiteSpace(stream.UserName) ? stream.UserLogin! : stream.UserName!,
                stream.GameName ?? string.Empty,
                (stream.Title ?? string.Empty).Trim(),
                stream.ViewerCount,
                stream.StartedAt,
                (stream.ThumbnailUrl ?? string.Empty).Replace("{width}", "320").Replace("{height}", "180")))
            .OrderByDescending(stream => stream.ViewerCount)
            .ToArray();
    }

    /// <summary>
    /// Chat badges with their pictures: Twitch's global ones, or a channel's own (custom sub and bits badges)
    /// when <paramref name="broadcasterId"/> is given.
    /// </summary>
    public async Task<IReadOnlyList<(TwitchBadge Badge, TwitchBadgeImage Image)>> GetBadgesAsync(string? broadcasterId, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrEmpty(broadcasterId)
            ? $"{ApiBase}/chat/badges/global"
            : $"{ApiBase}/chat/badges?broadcaster_id={Uri.EscapeDataString(broadcasterId)}";
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<HelixPage<HelixBadgeSet>>(cancellationToken).ConfigureAwait(false);
        return ToBadges(page?.Data);
    }

    internal static IReadOnlyList<(TwitchBadge Badge, TwitchBadgeImage Image)> ToBadges(IEnumerable<HelixBadgeSet>? sets)
    {
        var badges = new List<(TwitchBadge, TwitchBadgeImage)>();
        foreach (var set in sets ?? [])
        {
            if (string.IsNullOrEmpty(set.SetId))
                continue;

            foreach (var version in set.Versions ?? [])
            {
                var url = version.ImageUrl2x is { Length: > 0 } sharp ? sharp : version.ImageUrl1x;
                if (string.IsNullOrEmpty(version.Id) || string.IsNullOrEmpty(url))
                    continue;

                badges.Add((new TwitchBadge(set.SetId, version.Id), new TwitchBadgeImage(url, version.Title ?? string.Empty)));
            }
        }

        return badges;
    }

    /// <summary>Sends a chat message as the user. Twitch says why when it drops one (slow mode, followers-only…).</summary>
    public async Task<TwitchSendResult> SendChatMessageAsync(string channelLogin, string message, CancellationToken cancellationToken)
    {
        try
        {
            var senderId = UserId;
            var broadcasterId = await GetUserIdAsync(channelLogin, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(broadcasterId))
                return new TwitchSendResult(false, "Couldn't find that channel on Twitch.");

            using var response = await SendApiAsync(
                () => new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/chat/messages")
                {
                    Content = JsonContent.Create(new { broadcaster_id = broadcasterId, sender_id = senderId, message }),
                },
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadSendResult(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TwitchSendResult(false, ex.GetBaseException().Message);
        }
    }

    internal static TwitchSendResult ReadSendResult(HttpStatusCode status, string body)
    {
        try
        {
            if ((int)status is >= 200 and < 300)
            {
                var page = JsonSerializer.Deserialize<HelixPage<HelixSent>>(body);
                var sent = page?.Data?.FirstOrDefault();
                if (sent == null)
                    return new TwitchSendResult(false, "Twitch didn't say whether it was sent.");

                return sent.IsSent
                    ? new TwitchSendResult(true, string.Empty, sent.MessageId ?? string.Empty)
                    : new TwitchSendResult(false, sent.DropReason?.Message is { Length: > 0 } reason ? reason : "Twitch didn't send it.");
            }

            var error = JsonSerializer.Deserialize<TwitchError>(body);
            return new TwitchSendResult(false, error?.Message is { Length: > 0 } message ? message : $"Twitch answered {(int)status}.");
        }
        catch (JsonException)
        {
            return new TwitchSendResult(false, $"Twitch answered {(int)status}.");
        }
    }

    private async Task<string> GetUserIdAsync(string login, CancellationToken cancellationToken)
    {
        if (userIds.TryGetValue(login, out var cached))
            return cached;

        using var response = await SendApiAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users?login={Uri.EscapeDataString(login)}"),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<HelixPage<HelixUser>>(cancellationToken).ConfigureAwait(false);
        var id = page?.Data?.FirstOrDefault()?.Id ?? string.Empty;
        if (id.Length > 0)
            userIds[login] = id;
        return id;
    }

    /// <summary>Sends an API request with the user's token, refreshing it once if Twitch says it expired.</summary>
    private async Task<HttpResponseMessage> SendApiAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Not logged in to Twitch.");
            var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Client-Id", clientId);
            var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                return response;

            response.Dispose();
            if (!await RefreshAsync(token, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Twitch logged you out. Log in again.");
        }
    }

    /// <summary>
    /// The emotes the user can send in <paramref name="broadcasterId"/>'s chat: global ones, their subscriptions'
    /// and Bits emotes, and that channel's follower emotes. A login from before the emote permission gets
    /// Twitch's global emotes instead, with <see cref="TwitchEmoteList.MissingScope"/> set.
    /// </summary>
    public async Task<TwitchEmoteList> GetEmotesAsync(string broadcasterId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId))
            return new TwitchEmoteList([], false);

        var emotes = new List<TwitchEmote>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var url = $"{ApiBase}/chat/emotes/user?user_id={Uri.EscapeDataString(userId)}";
            if (!string.IsNullOrEmpty(broadcasterId))
                url += $"&broadcaster_id={Uri.EscapeDataString(broadcasterId)}";
            if (cursor != null)
                url += $"&after={Uri.EscapeDataString(cursor)}";

            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
            // Twitch answers 401 when the token lacks user:read:emotes.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new TwitchEmoteList(await GetGlobalEmotesAsync(cancellationToken).ConfigureAwait(false), true);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<HelixEmotePage>(cancellationToken).ConfigureAwait(false);
            emotes.AddRange(ToEmotes(result?.Data));
            cursor = result?.Pagination?.Cursor;
            if (string.IsNullOrEmpty(cursor))
                break;
        }

        return new TwitchEmoteList(emotes, false);
    }

    private async Task<IReadOnlyList<TwitchEmote>> GetGlobalEmotesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/chat/emotes/global"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<HelixEmotePage>(cancellationToken).ConfigureAwait(false);
        return ToEmotes(result?.Data, defaultType: "globals");
    }

    internal static IReadOnlyList<TwitchEmote> ToEmotes(IEnumerable<HelixEmote>? emotes, string defaultType = "")
    {
        return (emotes ?? [])
            .Where(emote => !string.IsNullOrWhiteSpace(emote.Id) && !string.IsNullOrWhiteSpace(emote.Name))
            .Select(emote => new TwitchEmote(
                emote.Id!,
                emote.Name!,
                string.IsNullOrEmpty(emote.EmoteType) ? defaultType : emote.EmoteType,
                emote.OwnerId ?? string.Empty))
            .ToArray();
    }

    // ---------------------------------------------------------------- tokens

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = tokens;
        if (current == null)
            return null;

        if (current.ExpiresAt - RefreshMargin > DateTimeOffset.UtcNow)
            return current.AccessToken;

        return await RefreshAsync(current.AccessToken, cancellationToken).ConfigureAwait(false) ? tokens?.AccessToken : null;
    }

    /// <summary>
    /// Gets a new token with the refresh token. Twitch's refresh tokens work once each, so only one refresh
    /// runs at a time, and a caller whose token was already replaced just uses the new one.
    /// </summary>
    private async Task<bool> RefreshAsync(string expiredToken, CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = tokens;
            if (current == null)
                return false;
            if (current.AccessToken != expiredToken)
                return true;

            using var response = await http.PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = current.RefreshToken,
            }), cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                LoseLogin("Twitch logged you out. Log in again.");
                return false;
            }

            response.EnsureSuccessStatusCode();
            var answer = await response.Content.ReadFromJsonAsync<TokenAnswer>(cancellationToken).ConfigureAwait(false);
            if (answer?.AccessToken is not { Length: > 0 })
                return false;

            Store(current with
            {
                AccessToken = answer.AccessToken,
                RefreshToken = answer.RefreshToken is { Length: > 0 } refresh ? refresh : current.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, answer.ExpiresIn)),
            });
            return true;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var codeResponse = await http.PostAsync($"{IdBase}/device", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = Scopes,
            }), cancellationToken).ConfigureAwait(false);
            codeResponse.EnsureSuccessStatusCode();
            var code = await codeResponse.Content.ReadFromJsonAsync<DeviceAnswer>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Twitch sent no login code.");

            var device = new TwitchDeviceCode(
                code.DeviceCode,
                code.UserCode,
                code.VerificationUri,
                DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn),
                Math.Max(1, code.Interval));
            pendingCode = device;
            state = TwitchAccountState.WaitingForApproval;

            var answer = await PollForTokenAsync(device, cancellationToken).ConfigureAwait(false);
            if (answer == null)
            {
                pendingCode = null;
                state = TwitchAccountState.SignedOut;
                lastError = "The login code expired. Try again.";
                return;
            }

            var user = await ValidateAsync(answer.AccessToken, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Twitch didn't accept the new login.");
            var displayName = await GetDisplayNameAsync(answer.AccessToken, cancellationToken).ConfigureAwait(false);
            Store(new TwitchTokens(
                answer.AccessToken,
                answer.RefreshToken ?? string.Empty,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, answer.ExpiresIn)),
                user.Login ?? string.Empty,
                user.UserId ?? string.Empty,
                displayName));
            pendingCode = null;
            state = TwitchAccountState.SignedIn;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "Twitch login failed.");
            pendingCode = null;
            state = TwitchAccountState.SignedOut;
            lastError = $"Twitch login failed: {ex.GetBaseException().Message}";
        }
    }

    /// <summary>Asks for the token every few seconds until the user approves, or null when the code expires.</summary>
    private async Task<TokenAnswer?> PollForTokenAsync(TwitchDeviceCode device, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(device.IntervalSeconds);
        while (DateTimeOffset.UtcNow < device.ExpiresAt)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            using var response = await http.PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = Scopes,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }), cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return JsonSerializer.Deserialize<TokenAnswer>(body);

            switch (ReadPollError(body))
            {
                case PollError.Pending:
                    continue;
                case PollError.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case PollError.Expired:
                    return null;
                default:
                    throw new InvalidOperationException(JsonSerializer.Deserialize<TwitchError>(body)?.Message ?? $"Twitch answered {(int)response.StatusCode}.");
            }
        }

        return null;
    }

    internal enum PollError
    {
        Pending,
        SlowDown,
        Expired,
        Other,
    }

    internal static PollError ReadPollError(string body)
    {
        string? message;
        try
        {
            message = JsonSerializer.Deserialize<TwitchError>(body)?.Message;
        }
        catch (JsonException)
        {
            return PollError.Other;
        }

        return message switch
        {
            "authorization_pending" => PollError.Pending,
            "slow_down" => PollError.SlowDown,
            // Twitch says this when the code ran out or the user pressed Decline.
            "invalid device code" => PollError.Expired,
            _ => PollError.Other,
        };
    }

    private async Task<ValidateAnswer?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{IdBase}/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<ValidateAnswer>(cancellationToken).ConfigureAwait(false);
        if (answer?.Scopes is { } scopes)
            grantedScopes = scopes;
        return answer;
    }

    private async Task<string> GetDisplayNameAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("Client-Id", clientId);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<HelixPage<HelixUser>>(cancellationToken).ConfigureAwait(false);
            return page?.Data?.FirstOrDefault()?.DisplayName ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>Twitch asks apps to check their tokens every hour and to stop using ones it revoked.</summary>
    private async Task ValidateLoopAsync()
    {
        var first = true;
        while (!disposal.IsCancellationRequested)
        {
            try
            {
                if (!first)
                    await Task.Delay(ValidateInterval, disposal.Token).ConfigureAwait(false);
                first = false;

                var current = tokens;
                if (current == null)
                    continue;

                var user = await ValidateAsync(current.AccessToken, disposal.Token).ConfigureAwait(false);
                if (user == null)
                    await RefreshAsync(current.AccessToken, disposal.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (disposal.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Offline or Twitch unreachable: keep the login and try again next hour.
                log?.Debug(ex, "Twitch token check failed.");
            }
        }
    }

    private async Task RevokeAsync(string accessToken)
    {
        try
        {
            using var _ = await http.PostAsync($"{IdBase}/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["token"] = accessToken,
            }), disposal.Token).ConfigureAwait(false);
        }
        catch
        {
            // Logged out here either way; the token expires on its own.
        }
    }

    private void Store(TwitchTokens updated)
    {
        lock (sync)
            tokens = updated;
        try
        {
            store.Save(updated);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "Could not save the Twitch login.");
        }
    }

    private void LoseLogin(string reason)
    {
        lock (sync)
            tokens = null;
        store.Delete();
        state = TwitchAccountState.SignedOut;
        lastError = reason;
    }

    // ---------------------------------------------------------------- JSON shapes

    private sealed class DeviceAnswer
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    private sealed class TokenAnswer
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class ValidateAnswer
    {
        [JsonPropertyName("scopes")] public string[]? Scopes { get; set; }
        [JsonPropertyName("login")] public string? Login { get; set; }
        [JsonPropertyName("user_id")] public string? UserId { get; set; }
    }

    private sealed class TwitchError
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    internal sealed class HelixPage<T>
    {
        [JsonPropertyName("data")] public List<T>? Data { get; set; }
    }

    internal sealed class HelixStream
    {
        [JsonPropertyName("user_id")] public string? UserId { get; set; }
        [JsonPropertyName("user_login")] public string? UserLogin { get; set; }
        [JsonPropertyName("user_name")] public string? UserName { get; set; }
        [JsonPropertyName("game_name")] public string? GameName { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("viewer_count")] public int ViewerCount { get; set; }
        [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("thumbnail_url")] public string? ThumbnailUrl { get; set; }
    }

    internal sealed class HelixBadgeSet
    {
        [JsonPropertyName("set_id")] public string? SetId { get; set; }
        [JsonPropertyName("versions")] public List<HelixBadgeVersion>? Versions { get; set; }
    }

    internal sealed class HelixBadgeVersion
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("image_url_1x")] public string? ImageUrl1x { get; set; }
        [JsonPropertyName("image_url_2x")] public string? ImageUrl2x { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    private sealed class HelixUser
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }

    private sealed class HelixSent
    {
        [JsonPropertyName("message_id")] public string? MessageId { get; set; }
        [JsonPropertyName("is_sent")] public bool IsSent { get; set; }
        [JsonPropertyName("drop_reason")] public HelixDropReason? DropReason { get; set; }
    }

    private sealed class HelixDropReason
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
