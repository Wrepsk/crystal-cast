using System.Net;
using System.Text;
using CrystalCast.Browser.WebView2;
using CrystalCast.Sources;
using CrystalCast.Twitch;
using CrystalCast.Windows.Panels;

namespace CrystalCast.Tests;

public sealed class TwitchAccountTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CrystalCastTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task LogsInWithADeviceCodeAndKeepsTheLogin()
    {
        var twitch = new FakeTwitch();
        twitch.On("POST", "/oauth2/device", 200, """{"device_code":"dev","user_code":"ABCD1234","verification_uri":"https://www.twitch.tv/activate?public=true&device-code=ABCD1234","expires_in":1800,"interval":1}""");
        // First poll: not approved yet; second: approved.
        twitch.On("POST", "/oauth2/token", 400, """{"status":400,"message":"authorization_pending"}""");
        twitch.On("POST", "/oauth2/token", 200, """{"access_token":"at1","refresh_token":"rt1","expires_in":14000,"scope":["user:read:follows"],"token_type":"bearer"}""");
        twitch.On("GET", "/oauth2/validate", 200, """{"client_id":"cid","login":"viewer","user_id":"42","expires_in":14000}""");
        twitch.On("GET", "/helix/users", 200, """{"data":[{"id":"42","display_name":"Viewer"}]}""");

        using (var account = new TwitchAccount(new TwitchTokenStore(directory), null, "cid", twitch))
        {
            account.BeginSignIn();
            await WaitFor(() => account.PendingCode != null);
            Assert.Equal(TwitchAccountState.WaitingForApproval, account.State);
            Assert.Equal("ABCD1234", account.PendingCode!.UserCode);

            await WaitFor(() => account.State == TwitchAccountState.SignedIn);
            Assert.Equal("Viewer", account.DisplayName);
            Assert.Equal("42", account.UserId);
            Assert.Contains("scopes=user%3Aread%3Afollows+user%3Awrite%3Achat", twitch.Bodies["/oauth2/device"]);
        }

        // A new session reads the saved login.
        using var reloaded = new TwitchAccount(new TwitchTokenStore(directory), null, "cid", new FakeTwitch());
        Assert.Equal(TwitchAccountState.SignedIn, reloaded.State);
        Assert.Equal("viewer", reloaded.Login);
    }

    [Fact]
    public async Task RefreshesAnExpiredTokenAndRetries()
    {
        var store = new TwitchTokenStore(directory);
        store.Save(new TwitchTokens("old", "rt1", DateTimeOffset.UtcNow.AddHours(1), "viewer", "42", "Viewer"));

        var twitch = new FakeTwitch();
        twitch.On("GET", "/oauth2/validate", 200, """{"login":"viewer","user_id":"42"}""");
        twitch.On("GET", "/helix/streams/followed", 401, """{"message":"Invalid OAuth token"}""");
        twitch.On("POST", "/oauth2/token", 200, """{"access_token":"new","refresh_token":"rt2","expires_in":14000}""");
        twitch.On("GET", "/helix/streams/followed", 200, """{"data":[{"user_id":"7","user_login":"Streamer","user_name":"Streamer","game_name":"FINAL FANTASY XIV ONLINE","title":" Raid night ","viewer_count":1234,"started_at":"2026-11-16T18:00:00Z","thumbnail_url":"https://static-cdn.jtvnw.net/previews-ttv/live_user_streamer-{width}x{height}.jpg"}]}""");

        using var account = new TwitchAccount(store, null, "cid", twitch);
        var streams = await account.GetFollowedLiveAsync(CancellationToken.None);

        var stream = Assert.Single(streams);
        Assert.Equal("streamer", stream.Login);
        Assert.Equal("Raid night", stream.Title);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_streamer-320x180.jpg", stream.ThumbnailUrl);
        Assert.Equal("Bearer new", twitch.LastAuthorization["/helix/streams/followed"]);
        Assert.Equal("new", new TwitchTokenStore(directory).Load()!.AccessToken);
    }

    [Fact]
    public async Task LosesTheLoginWhenTwitchRefusesTheRefresh()
    {
        var store = new TwitchTokenStore(directory);
        store.Save(new TwitchTokens("old", "rt1", DateTimeOffset.UtcNow.AddMinutes(-5), "viewer", "42", "Viewer"));
        var twitch = new FakeTwitch();
        twitch.On("GET", "/oauth2/validate", 401, """{"message":"invalid access token"}""");
        twitch.On("POST", "/oauth2/token", 400, """{"message":"Invalid refresh token"}""");

        using var account = new TwitchAccount(store, null, "cid", twitch);
        // The hourly check at startup may get there first; either way the login is gone.
        try
        {
            Assert.Empty(await account.GetFollowedLiveAsync(CancellationToken.None));
        }
        catch (InvalidOperationException)
        {
        }

        await WaitFor(() => account.State == TwitchAccountState.SignedOut);

        Assert.Equal(TwitchAccountState.SignedOut, account.State);
        Assert.Null(new TwitchTokenStore(directory).Load());
    }

    [Fact]
    public void LoginIsOffWithoutAClientId()
    {
        using var account = new TwitchAccount(new TwitchTokenStore(directory), null, string.Empty, new FakeTwitch());
        Assert.False(account.Configured);
        account.BeginSignIn();
        Assert.Equal(TwitchAccountState.SignedOut, account.State);
    }

    [Theory]
    [InlineData("""{"status":400,"message":"authorization_pending"}""", "Pending")]
    [InlineData("""{"status":400,"message":"slow_down"}""", "SlowDown")]
    [InlineData("""{"status":400,"message":"invalid device code"}""", "Expired")]
    [InlineData("not json", "Other")]
    public void ReadsPollAnswers(string body, string expected)
    {
        Assert.Equal(expected, TwitchAccount.ReadPollError(body).ToString());
    }

    [Fact]
    public void ReadsSendAnswers()
    {
        Assert.True(TwitchAccount.ReadSendResult(HttpStatusCode.OK, """{"data":[{"message_id":"m","is_sent":true}]}""").Sent);

        var dropped = TwitchAccount.ReadSendResult(HttpStatusCode.OK, """{"data":[{"is_sent":false,"drop_reason":{"code":"followers_only","message":"This room is in followers-only mode."}}]}""");
        Assert.False(dropped.Sent);
        Assert.Equal("This room is in followers-only mode.", dropped.Error);

        var forbidden = TwitchAccount.ReadSendResult(HttpStatusCode.Forbidden, """{"error":"Forbidden","status":403,"message":"You are banned."}""");
        Assert.Equal("You are banned.", forbidden.Error);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/activate?device-code=X", "twitch.tv", true)]
    [InlineData("https://passport.twitch.tv/login", "twitch.tv", true)]
    [InlineData("http://www.twitch.tv/activate", "twitch.tv", false)]
    [InlineData("https://twitch.tv.example.com/", "twitch.tv", false)]
    [InlineData("https://www.twitch.tv/activate", null, false)]
    public void AllowsOnlyTheLoginSiteDuringALogin(string url, string? domain, bool allowed)
    {
        Assert.Equal(allowed, BrowserSignInPolicy.IsAllowed(url, domain == null ? null : new BrowserSignIn(url, [domain])));
    }

    [Theory]
    [InlineData(1, "1 viewer")]
    [InlineData(999, "999 viewers")]
    [InlineData(1234, "1.2K viewers")]
    [InlineData(2_500_000, "2.5M viewers")]
    public void FormatsViewerCounts(int viewers, string expected)
    {
        Assert.Equal(expected, TwitchPanel.FormatViewers(viewers));
    }

    [Fact]
    public void TokenStoreRoundTripsAndDeletes()
    {
        var store = new TwitchTokenStore(directory);
        var tokens = new TwitchTokens("at", "rt", DateTimeOffset.UnixEpoch, "viewer", "42", "Viewer");
        store.Save(tokens);

        Assert.Equal(tokens, store.Load());
        // Encrypted: the token isn't readable in the file.
        Assert.DoesNotContain("viewer", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "twitch-login.bin"))));

        store.Delete();
        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData("SignedIn", false, true)]
    [InlineData("SignedIn", true, false)]
    [InlineData("SignedIn", null, false)]
    [InlineData("SignedOut", false, false)]
    [InlineData("WaitingForApproval", false, false)]
    public void SaysWhenOnlyThePluginIsLoggedIn(string state, bool? playerLoggedIn, bool shown)
    {
        Assert.Equal(shown, TwitchPanel.ShowsPlayerLoggedOutNote(Enum.Parse<TwitchAccountState>(state), playerLoggedIn));
    }

    [Fact]
    public async Task ListsTheUsersEmotesAcrossPages()
    {
        var store = new TwitchTokenStore(directory);
        store.Save(new TwitchTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "viewer", "42", "Viewer"));
        var twitch = new FakeTwitch();
        twitch.On("GET", "/oauth2/validate", 200, """{"login":"viewer","user_id":"42"}""");
        twitch.On("GET", "/helix/chat/emotes/user", 200, """{"data":[{"id":"25","name":"Kappa","emote_type":"globals","owner_id":"0"},{"id":"e1","name":"chanHype","emote_type":"subscriptions","owner_id":"7"}],"pagination":{"cursor":"next"}}""");
        twitch.On("GET", "/helix/chat/emotes/user", 200, """{"data":[{"id":"e2","name":"otherWave","emote_type":"subscriptions","owner_id":"9"},{"id":"","name":"broken"}],"pagination":{}}""");

        using var account = new TwitchAccount(store, null, "cid", twitch);
        var list = await account.GetEmotesAsync("7", CancellationToken.None);

        Assert.False(list.MissingScope);
        Assert.Equal(["Kappa", "chanHype", "otherWave"], list.Emotes.Select(emote => emote.Name));
        Assert.Contains("broadcaster_id=7", twitch.LastQuery["/helix/chat/emotes/user"]);
        Assert.Contains("after=next", twitch.LastQuery["/helix/chat/emotes/user"]);
    }

    [Fact]
    public async Task FallsBackToGlobalEmotesForOlderLogins()
    {
        var store = new TwitchTokenStore(directory);
        store.Save(new TwitchTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "viewer", "42", "Viewer"));
        var twitch = new FakeTwitch();
        twitch.On("GET", "/oauth2/validate", 200, """{"login":"viewer","user_id":"42"}""");
        twitch.On("GET", "/helix/chat/emotes/user", 401, """{"message":"Missing scope: user:read:emotes"}""");
        twitch.On("POST", "/oauth2/token", 200, """{"access_token":"at2","refresh_token":"rt2","expires_in":14000}""");
        twitch.On("GET", "/helix/chat/emotes/global", 200, """{"data":[{"id":"25","name":"Kappa"}]}""");

        using var account = new TwitchAccount(store, null, "cid", twitch);
        var list = await account.GetEmotesAsync("7", CancellationToken.None);

        Assert.True(list.MissingScope);
        var kappa = Assert.Single(list.Emotes);
        Assert.Equal("globals", kappa.Type);
        Assert.Equal(TwitchAccountState.SignedIn, account.State);
    }

    [Fact]
    public void AsksForTheEmotePermission()
    {
        Assert.Contains("user:read:emotes", TwitchAccount.Scopes.Split(' '));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    /// <summary>Answers by method and path, in order; the last answer for a path repeats.</summary>
    private sealed class FakeTwitch : HttpMessageHandler
    {
        private readonly Dictionary<string, List<(int Status, string Body)>> answers = new();
        public Dictionary<string, string> Bodies { get; } = new();
        public Dictionary<string, string> LastAuthorization { get; } = new();
        public Dictionary<string, string> LastQuery { get; } = new();

        public void On(string method, string path, int status, string body)
        {
            var key = $"{method} {path}";
            if (!answers.TryGetValue(key, out var list))
                answers[key] = list = [];
            list.Add((status, body));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            LastQuery[path] = request.RequestUri.Query;
            if (request.Content != null)
                Bodies[path] = await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Headers.Authorization is { } authorization)
                LastAuthorization[path] = authorization.ToString();

            lock (answers)
            {
                if (!answers.TryGetValue($"{request.Method} {path}", out var list) || list.Count == 0)
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };

                var (status, body) = list[0];
                if (list.Count > 1)
                    list.RemoveAt(0);
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }
    }
}
