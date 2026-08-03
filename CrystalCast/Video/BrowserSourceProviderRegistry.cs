namespace CrystalCast.Video;

internal static class BrowserSourceProviderRegistry
{
    private const int MinBrowserWidth = 320;
    private const int MaxBrowserWidth = 3840;
    private const int MinBrowserHeight = 180;
    private const int MaxBrowserHeight = 2160;
    private const float MinCaptureFps = 1.0f;
    private const float MaxCaptureFps = 120.0f;

    private static readonly IReadOnlyDictionary<BrowserSourceProviderKind, BrowserSourceProviderDefinition> Providers =
        CreateDefinitions().ToDictionary(provider => provider.Kind);

    public static IReadOnlyList<BrowserProviderOption> Options { get; } = Providers.Values
        .Select(provider => new BrowserProviderOption(provider.Kind, provider.DisplayName, provider.Capabilities))
        .ToArray();

    public static bool IsSupported(BrowserSourceProviderKind provider) => Providers.ContainsKey(provider);

    public static BrowserProviderCapabilities GetCapabilities(BrowserSourceProviderKind provider)
    {
        return GetProvider(provider).Capabilities;
    }

    internal static IVideoFrameSource? CreateFrameSource(
        BrowserScreenProfile screen,
        BrowserMediaEngine enginePreference,
        IBrowserFrameSourceFactory factory,
        Func<string, bool>? genericWebNavigationAllowed = null,
        string? genericWebInputOverride = null)
    {
        if (!Providers.TryGetValue(screen.ProviderKind, out var provider))
            return null;

        var request = provider.CreateRequest(screen, enginePreference);
        if (screen.ProviderKind == BrowserSourceProviderKind.GenericWeb)
        {
            request = request with
            {
                Input = string.IsNullOrWhiteSpace(genericWebInputOverride) ? request.Input : genericWebInputOverride,
                IsNavigationAllowed = genericWebNavigationAllowed,
            };
        }

        return factory.Create(request);
    }

    public static string BuildFrameSourceSignature(BrowserScreenProfile screen, BrowserMediaEngine enginePreference)
    {
        if (!Providers.TryGetValue(screen.ProviderKind, out var provider))
            return string.Join('|', ScreenSourceKind.Browser, screen.ProviderKind);

        var dimensions = provider.GetDimensions(screen);
        return string.Join('|',
            ScreenSourceKind.Browser,
            screen.ProviderKind,
            provider.GetUrl(screen),
            dimensions.Width,
            dimensions.Height,
            enginePreference);
    }

    public static BrowserScreenRuntimeSnapshot GetSnapshot(BrowserScreenProfile screen)
    {
        var provider = GetProvider(screen.ProviderKind);
        return new BrowserScreenRuntimeSnapshot(
            provider.GetUrl(screen),
            provider.GetDimensions(screen),
            provider.GetRuntimeSettings(screen),
            provider.GetCaptureSettings(screen));
    }

    public static BrowserSourceDimensions GetDimensions(BrowserScreenProfile screen) => GetSnapshot(screen).Dimensions;

    public static BrowserSourceRuntimeSettings GetRuntimeSettings(BrowserScreenProfile screen) => GetSnapshot(screen).RuntimeSettings;

    public static float GetDetectedVideoFps(IVideoFrameSource? frameSource)
    {
        return frameSource is IBrowserFrameSourceRuntime source ? source.DetectedVideoFps : 0.0f;
    }

    public static void ApplyCaptureFps(IVideoFrameSource? frameSource, BrowserScreenProfile screen)
    {
        if (frameSource is not IBrowserFrameSourceRuntime source || source.ProviderKind != screen.ProviderKind)
            return;

        var capture = GetProvider(screen.ProviderKind).GetCaptureSettings(screen);
        if (capture.Manual)
        {
            source.UpdateCaptureFps(capture.FramesPerSecond);
        }
        else if (source.DetectedVideoFps > 0.0f)
        {
            source.UpdateCaptureFps(source.DetectedVideoFps);
        }
    }

    public static bool NormalizeProviderSettings(BrowserScreenProfile screen)
    {
        var changed = false;
        foreach (var provider in Providers.Values)
        {
            changed |= NormalizeInt(provider.GetWidth, provider.SetWidth, screen, MinBrowserWidth, MaxBrowserWidth, provider.DefaultDimensions.Width);
            changed |= NormalizeInt(provider.GetHeight, provider.SetHeight, screen, MinBrowserHeight, MaxBrowserHeight, provider.DefaultDimensions.Height);
            changed |= NormalizeFloat(provider.GetCaptureFps, provider.SetCaptureFps, screen, MinCaptureFps, MaxCaptureFps);
            changed |= NormalizeFloat(provider.GetVolume, provider.SetVolume, screen, 0.0f, 1.0f);
            if (provider.GetPlaybackRate != null && provider.SetPlaybackRate != null)
                changed |= NormalizeFloat(provider.GetPlaybackRate, provider.SetPlaybackRate, screen, 0.25f, 2.0f);
        }

        return changed;
    }

    private static BrowserSourceProviderDefinition GetProvider(BrowserSourceProviderKind provider)
    {
        return Providers.TryGetValue(provider, out var definition)
            ? definition
            : Providers[BrowserSourceProviderKind.YouTube];
    }

    private static bool NormalizeInt(
        Func<BrowserScreenProfile, int> get,
        Action<BrowserScreenProfile, int> set,
        BrowserScreenProfile screen,
        int minimum,
        int maximum,
        int defaultValue)
    {
        var current = get(screen);
        var normalized = current <= 0 ? defaultValue : Math.Clamp(current, minimum, maximum);
        if (current == normalized)
            return false;

        set(screen, normalized);
        return true;
    }

    private static bool NormalizeFloat(
        Func<BrowserScreenProfile, float> get,
        Action<BrowserScreenProfile, float> set,
        BrowserScreenProfile screen,
        float minimum,
        float maximum)
    {
        var current = get(screen);
        var normalized = float.IsFinite(current) ? Math.Clamp(current, minimum, maximum) : minimum;
        if (Math.Abs(current - normalized) <= 0.0001f)
            return false;

        set(screen, normalized);
        return true;
    }

    private static BrowserSourceProviderDefinition[] CreateDefinitions()
    {
        return
        [
            new(
                BrowserSourceProviderKind.YouTube,
                "YouTube",
                new BrowserProviderCapabilities(true, true, true, true),
                new BrowserSourceDimensions(1280, 720),
                screen => screen.YouTubeUrl,
                screen => new(screen.YouTubeBrowserWidth, screen.YouTubeBrowserHeight),
                screen => new(screen.YouTubeAudioEnabled, screen.YouTubeVolume, screen.YouTubePlaybackRate, screen.LoopYouTube, screen.YouTubePlaylistAutoplayNext),
                screen => new(screen.YouTubeCaptureFpsManual, screen.YouTubeCaptureFps),
                (screen, engine) => new(
                    BrowserSourceProviderKind.YouTube, BrowserSourceDescriptors.YouTube, screen.YouTubeUrl,
                    screen.YouTubeBrowserWidth, screen.YouTubeBrowserHeight, screen.YouTubeCaptureFps, engine,
                    screen.YouTubeAutoplay, screen.LoopYouTube, screen.YouTubePlaylistAutoplayNext,
                    screen.YouTubeAudioEnabled, screen.YouTubeVolume, screen.YouTubePlaybackRate),
                screen => screen.YouTubeBrowserWidth, (screen, value) => screen.YouTubeBrowserWidth = value,
                screen => screen.YouTubeBrowserHeight, (screen, value) => screen.YouTubeBrowserHeight = value,
                screen => screen.YouTubeCaptureFps, (screen, value) => screen.YouTubeCaptureFps = value,
                screen => screen.YouTubeVolume, (screen, value) => screen.YouTubeVolume = value,
                screen => screen.YouTubePlaybackRate, (screen, value) => screen.YouTubePlaybackRate = value),
            new(
                BrowserSourceProviderKind.Twitch,
                "Twitch",
                new BrowserProviderCapabilities(false, false, false, true),
                new BrowserSourceDimensions(1920, 1080),
                screen => screen.TwitchUrl,
                screen => new(screen.TwitchBrowserWidth, screen.TwitchBrowserHeight),
                screen => new(screen.TwitchAudioEnabled, screen.TwitchVolume, 1.0f, false, true),
                screen => new(screen.TwitchCaptureFpsManual, screen.TwitchCaptureFps),
                (screen, engine) => new(
                    BrowserSourceProviderKind.Twitch, BrowserSourceDescriptors.Twitch, screen.TwitchUrl,
                    screen.TwitchBrowserWidth, screen.TwitchBrowserHeight, screen.TwitchCaptureFps, engine,
                    screen.TwitchAutoplay, false, false, screen.TwitchAudioEnabled, screen.TwitchVolume, 1.0f),
                screen => screen.TwitchBrowserWidth, (screen, value) => screen.TwitchBrowserWidth = value,
                screen => screen.TwitchBrowserHeight, (screen, value) => screen.TwitchBrowserHeight = value,
                screen => screen.TwitchCaptureFps, (screen, value) => screen.TwitchCaptureFps = value,
                screen => screen.TwitchVolume, (screen, value) => screen.TwitchVolume = value,
                null, null),
            new(
                BrowserSourceProviderKind.Dailymotion,
                "Dailymotion",
                new BrowserProviderCapabilities(false, true, false, true),
                new BrowserSourceDimensions(1280, 720),
                screen => screen.DailymotionUrl,
                screen => new(screen.DailymotionBrowserWidth, screen.DailymotionBrowserHeight),
                screen => new(screen.DailymotionAudioEnabled, screen.DailymotionVolume, 1.0f, screen.LoopDailymotion, true),
                screen => new(screen.DailymotionCaptureFpsManual, screen.DailymotionCaptureFps),
                (screen, engine) => new(
                    BrowserSourceProviderKind.Dailymotion, BrowserSourceDescriptors.Dailymotion, screen.DailymotionUrl,
                    screen.DailymotionBrowserWidth, screen.DailymotionBrowserHeight, screen.DailymotionCaptureFps, engine,
                    screen.DailymotionAutoplay, screen.LoopDailymotion, true,
                    screen.DailymotionAudioEnabled, screen.DailymotionVolume, 1.0f),
                screen => screen.DailymotionBrowserWidth, (screen, value) => screen.DailymotionBrowserWidth = value,
                screen => screen.DailymotionBrowserHeight, (screen, value) => screen.DailymotionBrowserHeight = value,
                screen => screen.DailymotionCaptureFps, (screen, value) => screen.DailymotionCaptureFps = value,
                screen => screen.DailymotionVolume, (screen, value) => screen.DailymotionVolume = value,
                null, null),
            new(
                BrowserSourceProviderKind.Vimeo,
                "Vimeo",
                new BrowserProviderCapabilities(true, true, false, true),
                new BrowserSourceDimensions(1280, 720),
                screen => screen.VimeoUrl,
                screen => new(screen.VimeoBrowserWidth, screen.VimeoBrowserHeight),
                screen => new(screen.VimeoAudioEnabled, screen.VimeoVolume, screen.VimeoPlaybackRate, screen.LoopVimeo, true),
                screen => new(screen.VimeoCaptureFpsManual, screen.VimeoCaptureFps),
                (screen, engine) => new(
                    BrowserSourceProviderKind.Vimeo, BrowserSourceDescriptors.Vimeo, screen.VimeoUrl,
                    screen.VimeoBrowserWidth, screen.VimeoBrowserHeight, screen.VimeoCaptureFps, engine,
                    screen.VimeoAutoplay, screen.LoopVimeo, true,
                    screen.VimeoAudioEnabled, screen.VimeoVolume, screen.VimeoPlaybackRate),
                screen => screen.VimeoBrowserWidth, (screen, value) => screen.VimeoBrowserWidth = value,
                screen => screen.VimeoBrowserHeight, (screen, value) => screen.VimeoBrowserHeight = value,
                screen => screen.VimeoCaptureFps, (screen, value) => screen.VimeoCaptureFps = value,
                screen => screen.VimeoVolume, (screen, value) => screen.VimeoVolume = value,
                screen => screen.VimeoPlaybackRate, (screen, value) => screen.VimeoPlaybackRate = value),
            new(
                BrowserSourceProviderKind.GenericWeb,
                "Generic Web",
                new BrowserProviderCapabilities(true, true, false, true),
                new BrowserSourceDimensions(1280, 720),
                screen => screen.GenericWebUrl,
                screen => new(screen.GenericWebBrowserWidth, screen.GenericWebBrowserHeight),
                screen => new(screen.GenericWebAudioEnabled, screen.GenericWebVolume, screen.GenericWebPlaybackRate, screen.LoopGenericWeb, true),
                screen => new(screen.GenericWebCaptureFpsManual, screen.GenericWebCaptureFps),
                (screen, engine) => new(
                    BrowserSourceProviderKind.GenericWeb, null, screen.GenericWebUrl,
                    screen.GenericWebBrowserWidth, screen.GenericWebBrowserHeight, screen.GenericWebCaptureFps, engine,
                    screen.GenericWebAutoplay, screen.LoopGenericWeb, true,
                    screen.GenericWebAudioEnabled, screen.GenericWebVolume, screen.GenericWebPlaybackRate),
                screen => screen.GenericWebBrowserWidth, (screen, value) => screen.GenericWebBrowserWidth = value,
                screen => screen.GenericWebBrowserHeight, (screen, value) => screen.GenericWebBrowserHeight = value,
                screen => screen.GenericWebCaptureFps, (screen, value) => screen.GenericWebCaptureFps = value,
                screen => screen.GenericWebVolume, (screen, value) => screen.GenericWebVolume = value,
                screen => screen.GenericWebPlaybackRate, (screen, value) => screen.GenericWebPlaybackRate = value),
        ];
    }

    private sealed record BrowserSourceProviderDefinition(
        BrowserSourceProviderKind Kind,
        string DisplayName,
        BrowserProviderCapabilities Capabilities,
        BrowserSourceDimensions DefaultDimensions,
        Func<BrowserScreenProfile, string> GetUrl,
        Func<BrowserScreenProfile, BrowserSourceDimensions> GetDimensions,
        Func<BrowserScreenProfile, BrowserSourceRuntimeSettings> GetRuntimeSettings,
        Func<BrowserScreenProfile, BrowserCaptureSettings> GetCaptureSettings,
        Func<BrowserScreenProfile, BrowserMediaEngine, BrowserFrameSourceRequest> CreateRequest,
        Func<BrowserScreenProfile, int> GetWidth,
        Action<BrowserScreenProfile, int> SetWidth,
        Func<BrowserScreenProfile, int> GetHeight,
        Action<BrowserScreenProfile, int> SetHeight,
        Func<BrowserScreenProfile, float> GetCaptureFps,
        Action<BrowserScreenProfile, float> SetCaptureFps,
        Func<BrowserScreenProfile, float> GetVolume,
        Action<BrowserScreenProfile, float> SetVolume,
        Func<BrowserScreenProfile, float>? GetPlaybackRate,
        Action<BrowserScreenProfile, float>? SetPlaybackRate);
}

internal readonly record struct BrowserProviderOption(
    BrowserSourceProviderKind Kind,
    string DisplayName,
    BrowserProviderCapabilities Capabilities);

internal readonly record struct BrowserProviderCapabilities(
    bool SupportsPlaybackRate,
    bool SupportsLoop,
    bool SupportsPlaylist,
    bool SupportsBrowserControls);

internal readonly record struct BrowserScreenRuntimeSnapshot(
    string Url,
    BrowserSourceDimensions Dimensions,
    BrowserSourceRuntimeSettings RuntimeSettings,
    BrowserCaptureSettings CaptureSettings);

internal readonly record struct BrowserCaptureSettings(bool Manual, float FramesPerSecond);

internal readonly record struct BrowserSourceDimensions(int Width, int Height);

internal readonly record struct BrowserSourceRuntimeSettings(
    bool AudioEnabled,
    float Volume,
    float PlaybackRate,
    bool Loop,
    bool PlaylistAutoplayNext);
