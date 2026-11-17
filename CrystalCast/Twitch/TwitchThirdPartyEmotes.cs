using System.Net.Http.Headers;
using System.Text.Json;

namespace CrystalCast.Twitch;

/// <summary>
/// An emote from 7TV, BTTV or FFZ. These aren't Twitch emotes: Twitch sends them as plain words, and viewers
/// with one of those extensions see the word as a picture. <see cref="Aspect"/> is width over height (0 when the
/// service doesn't say). A zero-width emote is drawn over the emote before it.
/// </summary>
internal sealed record ThirdPartyEmote(string Name, string Url, float Aspect, bool ZeroWidth, string Provider);

/// <summary>
/// Loads a channel's 7TV, BTTV and FFZ emotes (and each service's global ones) from their public APIs, which
/// need no login. A service that's down or slow is skipped; the others still work. Channel emotes win over
/// global ones with the same name, and 7TV over BTTV over FFZ, as in common chat apps.
/// </summary>
internal sealed class TwitchThirdPartyEmotes : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private readonly HttpClient http;
    private Task<IReadOnlyList<ThirdPartyEmote>>? globals;

    public TwitchThirdPartyEmotes(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CrystalCast", typeof(TwitchThirdPartyEmotes).Assembly.GetName().Version?.ToString(3) ?? "1"));
    }

    public void Dispose() => http.Dispose();

    /// <summary>The emotes for the channel with Twitch user id <paramref name="channelId"/>, by name.</summary>
    public async Task<IReadOnlyDictionary<string, ThirdPartyEmote>> LoadAsync(string channelId, CancellationToken cancellationToken)
    {
        var id = Uri.EscapeDataString(channelId);
        var sevenTv = FetchAsync($"https://7tv.io/v3/users/twitch/{id}", ParseSevenTv, cancellationToken);
        var bttv = FetchAsync($"https://api.betterttv.net/3/cached/users/twitch/{id}", ParseBttvChannel, cancellationToken);
        var ffz = FetchAsync($"https://api.frankerfacez.com/v1/room/id/{id}", ParseFfz, cancellationToken);
        // Global lists rarely change; read once per session (again after a failure).
        globals = globals is { IsCompletedSuccessfully: true } ? globals : LoadGlobalsAsync(cancellationToken);

        await Task.WhenAll(sevenTv, bttv, ffz, globals).ConfigureAwait(false);
        return Merge([sevenTv.Result, bttv.Result, ffz.Result, globals.Result]);
    }

    private async Task<IReadOnlyList<ThirdPartyEmote>> LoadGlobalsAsync(CancellationToken cancellationToken)
    {
        var sevenTv = FetchAsync("https://7tv.io/v3/emote-sets/global", ParseSevenTv, cancellationToken);
        var bttv = FetchAsync("https://api.betterttv.net/3/cached/emotes/global", ParseBttvGlobal, cancellationToken);
        var ffz = FetchAsync("https://api.frankerfacez.com/v1/set/global", ParseFfz, cancellationToken);
        await Task.WhenAll(sevenTv, bttv, ffz).ConfigureAwait(false);
        return [.. sevenTv.Result, .. bttv.Result, .. ffz.Result];
    }

    /// <summary>One service's list, or none when it's down, slow, or the channel isn't on it.</summary>
    private async Task<IReadOnlyList<ThirdPartyEmote>> FetchAsync(string url, Func<string, IReadOnlyList<ThirdPartyEmote>> parse, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return [];

            return parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Earlier lists win: pass channel lists before global ones, 7TV before BTTV before FFZ.</summary>
    internal static IReadOnlyDictionary<string, ThirdPartyEmote> Merge(IEnumerable<IReadOnlyList<ThirdPartyEmote>> lists)
    {
        var byName = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            foreach (var emote in list)
                byName.TryAdd(emote.Name, emote);
        }

        return byName;
    }

    /// <summary>
    /// 7TV: a user (<c>emote_set.emotes</c>) or an emote set (<c>emotes</c>). Still PNGs only, since the game's
    /// image loader takes PNG everywhere but not reliably GIF, WebP or AVIF: the 2x PNG for still emotes, and
    /// 7TV's still frame (<c>2x_static.png</c>) for animated ones. The size comes from the listed files.
    /// </summary>
    internal static IReadOnlyList<ThirdPartyEmote> ParseSevenTv(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var emotes = root.TryGetProperty("emote_set", out var set) && set.ValueKind == JsonValueKind.Object
            ? Child(set, "emotes")
            : Child(root, "emotes");
        if (emotes.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ThirdPartyEmote>();
        foreach (var emote in emotes.EnumerateArray())
        {
            var name = Text(emote, "name");
            var data = Child(emote, "data");
            var host = Child(data, "host");
            var baseUrl = Text(host, "url");
            var files = Child(host, "files");
            if (name.Length == 0 || baseUrl.Length == 0 || files.ValueKind != JsonValueKind.Array)
                continue;

            var animated = Child(data, "animated").ValueKind == JsonValueKind.True;
            // An animated emote's still frame isn't in its file list, but has the same size as its 2x files.
            var listed = animated ? "2x.webp" : "2x.png";
            var wanted = animated ? "2x_static.png" : "2x.png";
            JsonElement? file = files.EnumerateArray().FirstOrDefault(candidate => Text(candidate, "name") == listed);
            if (file is not { ValueKind: JsonValueKind.Object } picked)
                continue;

            var width = Number(picked, "width");
            var height = Number(picked, "height");
            // 7TV marks zero-width emotes with flag 256 on the emote's data.
            var zeroWidth = ((long)Number(data, "flags") & 256) != 0;
            var url = baseUrl.StartsWith("//", StringComparison.Ordinal) ? $"https:{baseUrl}/{wanted}" : $"{baseUrl}/{wanted}";
            result.Add(new ThirdPartyEmote(name, url, height > 0 ? width / height : 0.0f, zeroWidth, "7TV"));
        }

        return result;
    }

    /// <summary>BTTV channel: <c>channelEmotes</c> and <c>sharedEmotes</c>, each <c>{ id, code }</c>.</summary>
    internal static IReadOnlyList<ThirdPartyEmote> ParseBttvChannel(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return [.. ParseBttvList(Child(root, "channelEmotes")), .. ParseBttvList(Child(root, "sharedEmotes"))];
    }

    /// <summary>BTTV global: an array of <c>{ id, code }</c>.</summary>
    internal static IReadOnlyList<ThirdPartyEmote> ParseBttvGlobal(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseBttvList(document.RootElement);
    }

    private static List<ThirdPartyEmote> ParseBttvList(JsonElement list)
    {
        var result = new List<ThirdPartyEmote>();
        if (list.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var emote in list.EnumerateArray())
        {
            var id = Text(emote, "id");
            var code = Text(emote, "code");
            if (id.Length == 0 || code.Length == 0)
                continue;

            // A PNG of every emote, the first frame for animated ones; BTTV doesn't list a size.
            result.Add(new ThirdPartyEmote(code, $"https://cdn.betterttv.net/emote/{Uri.EscapeDataString(id)}/2x.png", 0.0f, false, "BTTV"));
        }

        return result;
    }

    /// <summary>
    /// FFZ room or global: <c>sets</c> of <c>emoticons</c>, each with <c>name</c>, <c>width</c>, <c>height</c> and
    /// <c>urls</c> by scale.
    /// </summary>
    internal static IReadOnlyList<ThirdPartyEmote> ParseFfz(string json)
    {
        using var document = JsonDocument.Parse(json);
        var sets = Child(document.RootElement, "sets");
        if (sets.ValueKind != JsonValueKind.Object)
            return [];

        var result = new List<ThirdPartyEmote>();
        foreach (var set in sets.EnumerateObject())
        {
            var emoticons = Child(set.Value, "emoticons");
            if (emoticons.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var emote in emoticons.EnumerateArray())
            {
                var name = Text(emote, "name");
                var urls = Child(emote, "urls");
                var url = Text(urls, "2") is { Length: > 0 } sharp ? sharp : Text(urls, "1");
                if (name.Length == 0 || url.Length == 0)
                    continue;

                if (url.StartsWith("//", StringComparison.Ordinal))
                    url = $"https:{url}";
                var width = Number(emote, "width");
                var height = Number(emote, "height");
                result.Add(new ThirdPartyEmote(name, url, height > 0 ? width / height : 0.0f, false, "FFZ"));
            }
        }

        return result;
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static string Text(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;

    private static float Number(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.Number } value ? (float)value.GetDouble() : 0.0f;
}
