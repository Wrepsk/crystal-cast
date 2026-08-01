using System.Text.Json;

namespace CrystalCast.Video;

public interface IBrowserSourceReference
{
    string DisplayName { get; }
    string VideoId { get; }
}

internal delegate bool TryParseBrowserSource(string input, out IBrowserSourceReference source);

internal sealed class BrowserSourceDescriptor
{
    public required BrowserSourceProviderKind ProviderKind { get; init; }
    public required string DisplayName { get; init; }
    public required string InvalidSourceMessage { get; init; }
    public required string LoadReason { get; init; }
    public required BrowserMediaEngine PreferredAutoEngine { get; init; }
    public required TryParseBrowserSource TryParse { get; init; }
    public required Func<IBrowserSourceReference, BrowserPlaybackSettings, string> BuildHtml { get; init; }
    public required Func<IBrowserSourceReference, string, string> BuildCanonicalSourceUrl { get; init; }
    public required Func<string, bool> IsValidVideoId { get; init; }
    public required Func<JsonElement, string> DescribeError { get; init; }
    public JsonSerializerOptions JsonOptions { get; init; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string VirtualHostName { get; init; } = "crystalcast.local";
    public string PlayerOrigin => $"https://{VirtualHostName}";
    public string? WebView2AdditionalBrowserArguments { get; init; }
    public bool FailCefWhenReadyTimeoutExhausted { get; init; }
    public Func<JsonElement, string>? FormatStatusSuffix { get; init; }

    public string ParseInvalidInputStatus(string input)
    {
        return $"{InvalidSourceMessage}: {input}";
    }

    public string FormatPlayerStatus(string title, int stateCode, double positionSeconds, double durationSeconds, JsonElement root)
    {
        var suffix = FormatStatusSuffix?.Invoke(root) ?? string.Empty;
        return string.IsNullOrWhiteSpace(title)
            ? $"player state {stateCode}; {positionSeconds:0.0}s / {durationSeconds:0.0}s{suffix}"
            : $"{title}; state {stateCode}; {positionSeconds:0.0}s / {durationSeconds:0.0}s{suffix}";
    }
}

internal readonly record struct BrowserPlaybackSettings(
    bool Autoplay,
    bool Loop,
    bool PlaylistAutoplayNext,
    bool AudioEnabled,
    float Volume,
    float PlaybackRate);

internal interface IBrowserFrameSourceRuntime
{
    BrowserSourceProviderKind ProviderKind { get; }
    float DetectedVideoFps { get; }
    void UpdateCaptureFps(float fps);
}

internal interface IBrowserControlsHost
{
    bool BrowserControlsAvailable { get; }
    bool BrowserControlsVisible { get; }
    bool ShowBrowserControls();
    bool HideBrowserControls();
}

internal static class BrowserSourceDescriptors
{
    public static readonly BrowserSourceDescriptor YouTube = new()
    {
        ProviderKind = BrowserSourceProviderKind.YouTube,
        DisplayName = "YouTube",
        InvalidSourceMessage = "invalid YouTube URL, video ID, playlist, or live channel",
        LoadReason = "new video",
        PreferredAutoEngine = BrowserMediaEngine.WebView2Capture,
        TryParse = TryParseYouTube,
        BuildHtml = (source, settings) => YouTubePlayerPage.BuildHtml(
            (YouTubeSourceReference)source,
            settings.Autoplay,
            settings.Loop,
            settings.PlaylistAutoplayNext,
            settings.AudioEnabled,
            settings.Volume,
            settings.PlaybackRate),
        BuildCanonicalSourceUrl = (source, currentVideoId) => YouTubeVideoId.BuildCanonicalSourceUrl((YouTubeSourceReference)source, currentVideoId),
        IsValidVideoId = YouTubeVideoId.IsValidVideoId,
        DescribeError = root => DescribeProviderError(root, "YouTube", "IFrame API"),
        JsonOptions = YouTubePlayerPage.JsonOptions,
        WebView2AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
    };

    public static readonly BrowserSourceDescriptor Twitch = new()
    {
        ProviderKind = BrowserSourceProviderKind.Twitch,
        DisplayName = "Twitch",
        InvalidSourceMessage = "invalid Twitch channel or VOD URL",
        LoadReason = "new source",
        PreferredAutoEngine = BrowserMediaEngine.WebView2Capture,
        TryParse = TryParseTwitch,
        BuildHtml = (source, settings) => TwitchPlayerPage.BuildHtml(
            (TwitchSourceReference)source,
            settings.Autoplay,
            settings.AudioEnabled,
            settings.Volume),
        BuildCanonicalSourceUrl = (source, currentVideoId) => TwitchVideoId.BuildCanonicalSourceUrl((TwitchSourceReference)source, currentVideoId),
        IsValidVideoId = TwitchVideoId.IsValidVideoId,
        DescribeError = root => DescribeProviderError(root, "Twitch", "embed API"),
        JsonOptions = TwitchPlayerPage.JsonOptions,
        WebView2AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
        FailCefWhenReadyTimeoutExhausted = true,
    };

    public static readonly BrowserSourceDescriptor Dailymotion = new()
    {
        ProviderKind = BrowserSourceProviderKind.Dailymotion,
        DisplayName = "Dailymotion",
        InvalidSourceMessage = "invalid Dailymotion URL, video ID, or playlist",
        LoadReason = "new source",
        PreferredAutoEngine = BrowserMediaEngine.WebView2Capture,
        TryParse = TryParseDailymotion,
        BuildHtml = (source, settings) => DailymotionPlayerPage.BuildHtml(
            (DailymotionSourceReference)source,
            settings.Autoplay,
            settings.Loop,
            settings.AudioEnabled,
            settings.Volume),
        BuildCanonicalSourceUrl = (source, currentVideoId) => DailymotionVideoId.BuildCanonicalSourceUrl((DailymotionSourceReference)source, currentVideoId),
        IsValidVideoId = DailymotionVideoId.IsValidVideoId,
        DescribeError = root => DescribeProviderError(root, "Dailymotion", "player API"),
        JsonOptions = DailymotionPlayerPage.JsonOptions,
        WebView2AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
        FailCefWhenReadyTimeoutExhausted = true,
        FormatStatusSuffix = FormatDailymotionStatusSuffix,
    };

    public static readonly BrowserSourceDescriptor Vimeo = new()
    {
        ProviderKind = BrowserSourceProviderKind.Vimeo,
        DisplayName = "Vimeo",
        InvalidSourceMessage = "invalid Vimeo URL or video ID",
        LoadReason = "new source",
        PreferredAutoEngine = BrowserMediaEngine.WebView2Capture,
        TryParse = TryParseVimeo,
        BuildHtml = (source, settings) => VimeoPlayerPage.BuildHtml(
            (VimeoSourceReference)source,
            settings.Autoplay,
            settings.Loop,
            settings.AudioEnabled,
            settings.Volume,
            settings.PlaybackRate),
        BuildCanonicalSourceUrl = (source, currentVideoId) => VimeoVideoId.BuildCanonicalSourceUrl((VimeoSourceReference)source, currentVideoId),
        IsValidVideoId = VimeoVideoId.IsValidVideoId,
        DescribeError = root => DescribeProviderError(root, "Vimeo", "Player SDK"),
        JsonOptions = VimeoPlayerPage.JsonOptions,
        WebView2AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
        FailCefWhenReadyTimeoutExhausted = true,
    };

    public static readonly BrowserSourceDescriptor GenericWeb = new()
    {
        ProviderKind = BrowserSourceProviderKind.GenericWeb,
        DisplayName = "Generic Web",
        InvalidSourceMessage = "invalid Generic Web URL",
        LoadReason = "new page",
        PreferredAutoEngine = BrowserMediaEngine.WebView2Capture,
        TryParse = TryParseGenericWeb,
        BuildHtml = (_, _) => string.Empty,
        BuildCanonicalSourceUrl = (source, _) => ((GenericWebSourceReference)source).Url,
        IsValidVideoId = _ => true,
        DescribeError = root => DescribeProviderError(root, "Generic Web", "media controller"),
        WebView2AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
    };

    public static IReadOnlyList<BrowserSourceDescriptor> All { get; } =
    [
        YouTube,
        Twitch,
        Dailymotion,
        Vimeo,
        GenericWeb,
    ];

    public static BrowserSourceDescriptor Get(BrowserSourceProviderKind provider)
    {
        return provider switch
        {
            BrowserSourceProviderKind.Twitch => Twitch,
            BrowserSourceProviderKind.Dailymotion => Dailymotion,
            BrowserSourceProviderKind.Vimeo => Vimeo,
            BrowserSourceProviderKind.GenericWeb => GenericWeb,
            _ => YouTube,
        };
    }

    private static bool TryParseYouTube(string input, out IBrowserSourceReference source)
    {
        if (YouTubeVideoId.TryParseSource(input, out var parsed))
        {
            source = parsed;
            return true;
        }

        source = default!;
        return false;
    }

    private static bool TryParseTwitch(string input, out IBrowserSourceReference source)
    {
        if (TwitchVideoId.TryParseSource(input, out var parsed))
        {
            source = parsed;
            return true;
        }

        source = default!;
        return false;
    }

    private static bool TryParseDailymotion(string input, out IBrowserSourceReference source)
    {
        if (DailymotionVideoId.TryParseSource(input, out var parsed))
        {
            source = parsed;
            return true;
        }

        source = default!;
        return false;
    }

    private static bool TryParseVimeo(string input, out IBrowserSourceReference source)
    {
        if (VimeoVideoId.TryParseSource(input, out var parsed))
        {
            source = parsed;
            return true;
        }

        source = default!;
        return false;
    }

    private static bool TryParseGenericWeb(string input, out IBrowserSourceReference source)
    {
        if (GenericWebUrl.TryParseSource(input, out var parsed))
        {
            source = parsed;
            return true;
        }

        source = default!;
        return false;
    }

    private static string DescribeProviderError(JsonElement root, string providerName, string apiName)
    {
        if (root.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String)
            return BrowserMessageValidator.BoundText(messageProperty.GetString(), $"{providerName} player error");

        var code = TryGetInt(root, "code", 0);
        return code switch
        {
            2 => $"{providerName} player error: invalid video ID or parameter",
            5 => $"{providerName} player error: HTML5 playback failed",
            100 => $"{providerName} player error: video unavailable or private",
            101 or 150 => $"{providerName} player error: embedding is disallowed by the owner",
            153 => $"{providerName} player error: missing or blocked HTTP Referer",
            -1 => $"{providerName} player error: failed to load the {apiName}",
            _ => $"{providerName} player error: {code}",
        };
    }

    private static string FormatDailymotionStatusSuffix(JsonElement root)
    {
        var pipDisplay = TryGetString(root, "pipDisplay", string.Empty);
        var pipStatus = TryGetString(root, "pipStatus", string.Empty);
        var viewable = TryGetBool(root, "viewable", true);
        var embedMode = TryGetString(root, "embedMode", string.Empty);
        var muted = TryGetBool(root, "muted", false);
        var audioEnabled = TryGetBool(root, "audioEnabled", false);
        var pipDetail = string.IsNullOrWhiteSpace(pipStatus) && string.IsNullOrWhiteSpace(pipDisplay)
            ? string.Empty
            : $"; pip {pipStatus}/{pipDisplay}; viewable {viewable}";
        var embedDetail = string.IsNullOrWhiteSpace(embedMode)
            ? string.Empty
            : $"; {embedMode}; audio {(audioEnabled ? "on" : "off")}/{(muted ? "muted" : "unmuted")}";
        return pipDetail + embedDetail;
    }

    internal static string TryGetString(
        JsonElement root,
        string propertyName,
        string fallback,
        int maximumLength = BrowserMessageValidator.MaximumTextLength)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? BrowserMessageValidator.BoundText(property.GetString(), fallback, maximumLength)
            : fallback;
    }

    internal static double TryGetDouble(JsonElement root, string propertyName, double fallback)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.TryGetDouble(out var value)
            && double.IsFinite(value)
            ? value
            : fallback;
    }

    internal static int TryGetInt(JsonElement root, string propertyName, int fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    internal static bool TryGetBool(JsonElement root, string propertyName, bool fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
    }
}
