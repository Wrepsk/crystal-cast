using CrystalCast.Browser;
using CrystalCast.Playlist;
using CrystalCast.Video;

namespace CrystalCast.Sources;

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
        string? genericWebInputOverride = null,
        JpegCaptureResolution jpegResolution = JpegCaptureResolution.Full1080p)
    {
        if (!Providers.TryGetValue(screen.ProviderKind, out var provider))
            return null;

        var request = provider.CreateRequest(screen, enginePreference);
        request = request with
        {
            JpegResolution = jpegResolution,
            // With a playlist waiting, the video has to end so the next one can start.
            Loop = request.Loop && !ScreenPlaylistPolicy.OverridesVideoLoop(screen),
        };
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

    public static string BuildFrameSourceSignature(
        BrowserScreenProfile screen,
        BrowserMediaEngine enginePreference,
        JpegCaptureResolution jpegResolution = JpegCaptureResolution.Full1080p)
    {
        if (!Providers.TryGetValue(screen.ProviderKind, out var provider))
            return string.Join('|', ScreenSourceKind.Browser, screen.ProviderKind);

        var dimensions = provider.GetDimensions(screen);
        return string.Join('|',
            ScreenSourceKind.Browser,
            screen.ProviderKind,
            provider.Settings.GetUrl(screen),
            dimensions.Width,
            dimensions.Height,
            enginePreference,
            jpegResolution);
    }

    public static BrowserScreenRuntimeSnapshot GetSnapshot(BrowserScreenProfile screen)
    {
        var provider = GetProvider(screen.ProviderKind);
        var runtime = provider.GetRuntimeSettings(screen);
        if (runtime.Loop && ScreenPlaylistPolicy.OverridesVideoLoop(screen))
            runtime = runtime with { Loop = false };

        return new BrowserScreenRuntimeSnapshot(
            provider.Settings.GetUrl(screen),
            provider.GetDimensions(screen),
            runtime,
            provider.GetCaptureSettings(screen));
    }

    /// <summary>Sets the link for <paramref name="provider"/> without changing the screen's other settings.</summary>
    public static void SetUrl(BrowserScreenProfile screen, BrowserSourceProviderKind provider, string url)
    {
        if (!BrowserProviderSettings.IsSupported(provider))
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported browser source provider.");

        BrowserProviderSettings.For(provider).SetUrl(screen, url);
    }

    public static string GetUrl(BrowserScreenProfile screen) => BrowserProviderSettings.For(screen).GetUrl(screen);

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
            var settings = provider.Settings;
            changed |= NormalizeInt(settings.GetBrowserWidth, settings.SetBrowserWidth, screen, MinBrowserWidth, MaxBrowserWidth, provider.DefaultDimensions.Width);
            changed |= NormalizeInt(settings.GetBrowserHeight, settings.SetBrowserHeight, screen, MinBrowserHeight, MaxBrowserHeight, provider.DefaultDimensions.Height);
            changed |= NormalizeFloat(settings.GetCaptureFps, settings.SetCaptureFps, screen, MinCaptureFps, MaxCaptureFps);
            changed |= NormalizeFloat(settings.GetVolume, settings.SetVolume, screen, 0.0f, 1.0f);
            if (settings.HasPlaybackRate)
                changed |= NormalizeFloat(settings.GetPlaybackRate, settings.SetPlaybackRate, screen, 0.25f, 2.0f);
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
                BrowserSourceDescriptors.YouTube),
            new(
                BrowserSourceProviderKind.Twitch,
                "Twitch",
                new BrowserProviderCapabilities(false, false, false, true),
                new BrowserSourceDimensions(1920, 1080),
                BrowserSourceDescriptors.Twitch,
                PlaylistAutoplayNextInRequest: false),
            new(
                BrowserSourceProviderKind.Dailymotion,
                "Dailymotion",
                new BrowserProviderCapabilities(false, true, false, true),
                new BrowserSourceDimensions(1280, 720),
                BrowserSourceDescriptors.Dailymotion),
            new(
                BrowserSourceProviderKind.Vimeo,
                "Vimeo",
                new BrowserProviderCapabilities(true, true, false, true),
                new BrowserSourceDimensions(1280, 720),
                BrowserSourceDescriptors.Vimeo),
            new(
                BrowserSourceProviderKind.GenericWeb,
                "Generic Web",
                new BrowserProviderCapabilities(true, true, false, true),
                new BrowserSourceDimensions(1280, 720),
                RequestDescriptor: null),
        ];
    }

    /// <summary>
    /// What the registry knows about a provider beyond its per-screen settings, which
    /// <see cref="BrowserProviderSettings"/> reads.
    /// </summary>
    /// <param name="RequestDescriptor">The player page descriptor; null for Generic Web, which loads the page itself.</param>
    /// <param name="PlaylistAutoplayNextInRequest">False when a new frame source always starts with the setting off.</param>
    private sealed record BrowserSourceProviderDefinition(
        BrowserSourceProviderKind Kind,
        string DisplayName,
        BrowserProviderCapabilities Capabilities,
        BrowserSourceDimensions DefaultDimensions,
        BrowserSourceDescriptor? RequestDescriptor,
        bool PlaylistAutoplayNextInRequest = true)
    {
        public BrowserProviderSettings Settings { get; } = BrowserProviderSettings.For(Kind);

        public BrowserSourceDimensions GetDimensions(BrowserScreenProfile screen) =>
            new(Settings.GetBrowserWidth(screen), Settings.GetBrowserHeight(screen));

        public BrowserSourceRuntimeSettings GetRuntimeSettings(BrowserScreenProfile screen) => new(
            Settings.GetAudioEnabled(screen),
            Settings.GetVolume(screen),
            Settings.GetPlaybackRate(screen),
            Settings.GetLoop(screen),
            Settings.GetPlaylistAutoplayNext(screen));

        public BrowserCaptureSettings GetCaptureSettings(BrowserScreenProfile screen) =>
            new(Settings.GetCaptureFpsManual(screen), Settings.GetCaptureFps(screen));

        public BrowserFrameSourceRequest CreateRequest(BrowserScreenProfile screen, BrowserMediaEngine engine) => new(
            Kind,
            RequestDescriptor,
            Settings.GetUrl(screen),
            Settings.GetBrowserWidth(screen),
            Settings.GetBrowserHeight(screen),
            Settings.GetCaptureFps(screen),
            engine,
            Settings.GetAutoplay(screen),
            Settings.GetLoop(screen),
            PlaylistAutoplayNextInRequest && Settings.GetPlaylistAutoplayNext(screen),
            Settings.GetAudioEnabled(screen),
            Settings.GetVolume(screen),
            Settings.GetPlaybackRate(screen));
    }
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
