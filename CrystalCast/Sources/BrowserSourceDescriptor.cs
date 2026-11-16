using CrystalCast.Browser;
using CrystalCast.Sources.Providers;
using System.Text.Json;

namespace CrystalCast.Sources;

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

/// <summary>A browser source that measures how many frames per second it actually delivers.</summary>
internal interface ICaptureRateSource
{
    /// <summary>Frames captured per second over the last measurement window; 0 until measured.</summary>
    float MeasuredCaptureFps { get; }
}

internal interface IBrowserControlsHost
{
    bool BrowserControlsAvailable { get; }
    bool BrowserControlsVisible { get; }
    bool ShowBrowserControls();
    bool HideBrowserControls();
}

/// <summary>
/// A login shown in a player's browser: the page it starts on, the sites it may move between (each with its
/// subdomains, https only), and optionally the page that means it's done. Reaching that page ends the login
/// and returns to the player instead of loading it.
/// <para>
/// A site written <c>google.*</c> also covers that name's country sites (<c>google.de</c>,
/// <c>google.com.tr</c>, <c>google.co.uk</c>), which Google's login visits to set the session in each.
/// </para>
/// </summary>
internal sealed record BrowserSignIn(string Url, IReadOnlyList<string> Domains, string? FinishUrl = null);

/// <summary>Which pages a login may visit, and when it is done.</summary>
internal static class BrowserSignInPolicy
{
    /// <summary>A screen's status while its browser shows a login page.</summary>
    public const string ShowingLoginStatus = "WebView2 showing a login page";

    /// <summary>Starts a screen's status when a login was kept from a page; the page follows.</summary>
    public const string BlockedLoginStatusPrefix = "blocked login navigation to ";

    /// <summary>What other plugins see instead of a login status.</summary>
    public const string IpcLoginStatus = "logging in";

    /// <summary>
    /// A screen's status for other plugins. During a login it names the login site and page, which only the
    /// user needs (in Diagnostics); other plugins just learn that the screen is logging in.
    /// </summary>
    public static string StatusForIpc(string status, bool signingIn)
    {
        return signingIn
            || status.Contains(ShowingLoginStatus, StringComparison.Ordinal)
            || status.Contains(BlockedLoginStatusPrefix, StringComparison.Ordinal)
            ? IpcLoginStatus
            : status;
    }

    public static bool IsAllowed(string? url, BrowserSignIn? signIn)
    {
        return signIn != null
            && BrowserUriPolicy.TryCreateHttpUri(url, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && signIn.Domains.Any(domain => domain.EndsWith(".*", StringComparison.Ordinal)
                ? IsCountrySite(uri.IdnHost, domain[..^2])
                : BrowserUriPolicy.IsHostOrSubdomain(uri, domain));
    }

    /// <summary>
    /// True for <paramref name="name"/> under a top-level domain in the shapes country sites use, or a subdomain
    /// of one: <c>name.com</c>, <c>name.de</c>, <c>name.com.tr</c>, <c>name.co.uk</c>. Anything else after the
    /// name, such as <c>name.example.com</c>, doesn't count.
    /// </summary>
    internal static bool IsCountrySite(string host, string name)
    {
        var labels = host.ToLowerInvariant().TrimEnd('.').Split('.');
        var at = Array.LastIndexOf(labels, name.ToLowerInvariant());
        if (at < 0)
            return false;

        var suffix = labels[(at + 1)..];
        static bool IsCountry(string label) => label.Length == 2 && label.All(char.IsAsciiLetterLower);
        return suffix switch
        {
            ["com"] => true,
            [var country] => IsCountry(country),
            ["com" or "co", var country] => IsCountry(country),
            _ => false,
        };
    }

    /// <summary>
    /// A page for the log: scheme, host and path only. The query and fragment are left out, since during a
    /// login they carry codes and tokens.
    /// </summary>
    public static string Describe(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url is { Length: > 0 } ? url.Split('?', '#')[0] : "(empty)";

        return uri.Scheme is "http" or "https"
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}"
            : $"{uri.Scheme}:";
    }

    /// <summary>True for the finish page: same scheme, host and path; the query doesn't matter.</summary>
    public static bool IsFinish(string? url, BrowserSignIn? signIn)
    {
        return signIn?.FinishUrl != null
            && BrowserUriPolicy.TryCreateHttpUri(url, out var uri)
            && BrowserUriPolicy.TryCreateHttpUri(signIn.FinishUrl, out var finish)
            && Uri.Compare(uri, finish, UriComponents.Scheme | UriComponents.Host | UriComponents.Path, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }
}

/// <summary>
/// A player browser that can leave its player for a moment to show a site's own login page, the way Show
/// controls brings it forward, and then go back to the player. Used for logging in to Twitch and YouTube, so
/// the player plays as the user's account.
/// </summary>
internal interface IBrowserSignInHost
{
    /// <summary>True when this browser can show a login page now.</summary>
    bool SignInAvailable { get; }

    /// <summary>True while the login page is showing.</summary>
    bool SigningIn { get; }

    /// <summary>Opens the login in the player's browser and brings it forward.</summary>
    bool BeginSignIn(BrowserSignIn signIn);

    /// <summary>Goes back to the player and returns the browser to the screen.</summary>
    void EndSignIn();

    /// <summary>Deletes the browser's cookies, which logs it out of every site, and reloads the player.</summary>
    void SignOut();

    /// <summary>
    /// Whether the browser holds a cookie named <paramref name="name"/> for <paramref name="url"/>, which tells
    /// whether it's logged in to a site. Only presence is reported, never the value. Null when the browser
    /// isn't running.
    /// </summary>
    Task<bool?> HasCookieAsync(string url, string name);
}

internal static class BrowserSourceDescriptors
{
    public static readonly BrowserSourceDescriptor YouTube = new()
    {
        ProviderKind = BrowserSourceProviderKind.YouTube,
        DisplayName = "YouTube",
        InvalidSourceMessage = "invalid YouTube URL, video ID, playlist, or live channel",
        LoadReason = "new video",
        TryParse = Parser<YouTubeSourceReference>(YouTubeVideoId.TryParseSource),
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
        TryParse = Parser<TwitchSourceReference>(TwitchVideoId.TryParseSource),
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
    };

    public static readonly BrowserSourceDescriptor Dailymotion = new()
    {
        ProviderKind = BrowserSourceProviderKind.Dailymotion,
        DisplayName = "Dailymotion",
        InvalidSourceMessage = "invalid Dailymotion URL, video ID, or playlist",
        LoadReason = "new source",
        TryParse = Parser<DailymotionSourceReference>(DailymotionVideoId.TryParseSource),
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
        FormatStatusSuffix = FormatDailymotionStatusSuffix,
    };

    public static readonly BrowserSourceDescriptor Vimeo = new()
    {
        ProviderKind = BrowserSourceProviderKind.Vimeo,
        DisplayName = "Vimeo",
        InvalidSourceMessage = "invalid Vimeo URL or video ID",
        LoadReason = "new source",
        TryParse = Parser<VimeoSourceReference>(VimeoVideoId.TryParseSource),
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
    };

    public static readonly BrowserSourceDescriptor GenericWeb = new()
    {
        ProviderKind = BrowserSourceProviderKind.GenericWeb,
        DisplayName = "Generic Web",
        InvalidSourceMessage = "invalid Generic Web URL",
        LoadReason = "new page",
        TryParse = Parser<GenericWebSourceReference>(GenericWebUrl.TryParseSource),
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

    private delegate bool TryParseTypedSource<TSource>(string input, out TSource source);

    /// <summary>Lets a provider's typed link parser answer as the descriptor's parser.</summary>
    private static TryParseBrowserSource Parser<TSource>(TryParseTypedSource<TSource> tryParse)
        where TSource : struct, IBrowserSourceReference
    {
        return (string input, out IBrowserSourceReference source) =>
        {
            if (tryParse(input, out var parsed))
            {
                source = parsed;
                return true;
            }

            source = default!;
            return false;
        };
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
