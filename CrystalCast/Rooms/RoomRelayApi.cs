using System.Net.Http;
using CrystalCast.Ipc;

namespace CrystalCast.Rooms;

/// <summary>The relay's one plain HTTP call: creating a room.</summary>
internal static class RoomRelayApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The CrystalCast relay, used when the settings leave the address empty.</summary>
    public const string DefaultRelayUrl = "https://crystalcast-relay.crystalcast.workers.dev";

    /// <summary>The relay to use: the address from the settings, or the CrystalCast relay when it is empty.</summary>
    public static bool TryResolveRelay(string? configured, out Uri uri) =>
        TryParseRelayUri(string.IsNullOrWhiteSpace(configured) ? DefaultRelayUrl : configured, out uri);

    public static bool TryParseRelayUri(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static async Task<RoomCreateResponse> CreateRoomAsync(Uri relayBaseUri, CancellationToken token)
    {
        var endpoint = new Uri(relayBaseUri, $"{relayBaseUri.AbsolutePath.TrimEnd('/')}/rooms");
        using var response = await Http.PostAsync(endpoint, content: null, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        RoomCreateResponse? answer = null;
        try
        {
            answer = IpcJsonService.Deserialize<RoomCreateResponse>(body);
        }
        catch (System.Text.Json.JsonException)
        {
            // Handled below.
        }

        if (!response.IsSuccessStatusCode || answer == null || !RoomCode.TryNormalize(answer.Code, out var code) || string.IsNullOrEmpty(answer.HostKey))
        {
            return new RoomCreateResponse
            {
                Error = string.IsNullOrWhiteSpace(answer?.Error)
                    ? $"The relay answered {(int)response.StatusCode}."
                    : answer!.Error,
            };
        }

        answer.Code = code;
        return answer;
    }

    /// <summary>Makes a persistent room owned by <paramref name="memberKey"/>.</summary>
    public static async Task<PersistentRoomCreateResponse> CreatePersistentRoomAsync(
        Uri relayBaseUri,
        string name,
        string password,
        string memberKey,
        string username,
        CancellationToken token)
    {
        var endpoint = new Uri(relayBaseUri, $"{relayBaseUri.AbsolutePath.TrimEnd('/')}/persistent");
        var json = IpcJsonService.Serialize(new
        {
            name,
            password = RoomMemberKeys.HashPassword(password),
            key = memberKey,
            username = RoomUsername.Display(username),
        });
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(endpoint, content, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        PersistentRoomCreateResponse? answer = null;
        try
        {
            answer = IpcJsonService.Deserialize<PersistentRoomCreateResponse>(body);
        }
        catch (System.Text.Json.JsonException)
        {
            // Handled below.
        }

        if (!response.IsSuccessStatusCode || answer == null || !RoomCode.TryNormalize(answer.Code, out var code))
        {
            return new PersistentRoomCreateResponse
            {
                Error = string.IsNullOrWhiteSpace(answer?.Error)
                    ? response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "This relay doesn't support persistent rooms yet."
                        : $"The relay answered {(int)response.StatusCode}."
                    : answer!.Error,
            };
        }

        answer.Code = code;
        return answer;
    }
}
