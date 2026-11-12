using System.Text;
using System.Text.Json;

namespace CrystalCast.Video;

internal sealed class BrowserPlayerPageResource
{
    private BrowserPlayerPageResource(string url, string nonce, string html)
    {
        Url = url;
        Nonce = nonce;
        Html = html;
        Utf8Content = Encoding.UTF8.GetBytes(html);
    }

    public string Url { get; }
    public string Nonce { get; }
    public string Html { get; }
    public ReadOnlyMemory<byte> Utf8Content { get; }

    public static BrowserPlayerPageResource Create(
        BrowserSourceDescriptor descriptor,
        IBrowserSourceReference source,
        BrowserPlaybackSettings settings)
    {
        var nonce = BrowserPageMessaging.CreateNonce();
        var html = BrowserPageMessaging.AttachPageProtocol(descriptor.BuildHtml(source, settings), nonce);
        var instanceId = Guid.NewGuid().ToString("N");
        return new BrowserPlayerPageResource(
            $"{descriptor.PlayerOrigin}/player/{instanceId}.html",
            nonce,
            html);
    }
}

internal static class BrowserPageMessaging
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Lazy<string> CommandBridgeTemplate = new(
        () => BrowserAssetLoader.LoadText("CrystalCast.BrowserAssets.CommandBridge.js"));

    public static string CreateNonce() => Guid.NewGuid().ToString("N");

    public static string AttachPageProtocol(string html, string nonce)
    {
        html = AttachOutboundNonce(html, nonce);
        const string closingBody = "</body>";
        var bridge = $"<script>{BuildCommandBridge(nonce)}</script>";
        var index = html.LastIndexOf(closingBody, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? html + bridge : html.Insert(index, bridge);
    }

    public static string AttachOutboundNonce(string content, string nonce)
    {
        var nonceJson = JsonSerializer.Serialize(nonce);
        return content.Replace(
            "payload.type = type;",
            $"payload.type = type; payload.nonce = {nonceJson};",
            StringComparison.Ordinal);
    }

    public static string BuildCommandBridge(string nonce)
    {
        return CommandBridgeTemplate.Value.Replace(
            "__CRYSTALCAST_NONCE__",
            JsonSerializer.Serialize(nonce),
            StringComparison.Ordinal);
    }

    public static bool HasNonce(JsonElement root, string nonce)
    {
        return root.TryGetProperty("nonce", out var property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), nonce, StringComparison.Ordinal);
    }

    public static string Play(string nonce) => Serialize(new { type = "play", nonce });
    public static string Pause(string nonce) => Serialize(new { type = "pause", nonce });
    public static string Restart(string nonce) => Serialize(new { type = "restart", nonce });
    public static string SeekBy(string nonce, double seconds) => Serialize(new { type = "seekBy", nonce, seconds });
    public static string SeekTo(string nonce, double seconds) => Serialize(new { type = "seekTo", nonce, seconds });

    public static string Settings(string nonce, bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        return Serialize(new
        {
            type = "settings",
            nonce,
            settings = new
            {
                audioEnabled,
                volume,
                playbackRate,
                loop,
                playlistAutoplayNext,
            },
        });
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
