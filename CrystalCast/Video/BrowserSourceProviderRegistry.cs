namespace CrystalCast.Video;

internal static class BrowserSourceProviderRegistry
{
    private static readonly IReadOnlyDictionary<BrowserSourceProviderKind, IBrowserSourceProvider> Providers =
        new Dictionary<BrowserSourceProviderKind, IBrowserSourceProvider>
        {
            [BrowserSourceProviderKind.YouTube] = new YouTubeBrowserSourceProvider(),
            [BrowserSourceProviderKind.Twitch] = new TwitchBrowserSourceProvider(),
            [BrowserSourceProviderKind.Dailymotion] = new DailymotionBrowserSourceProvider(),
        };

    public static bool IsSupported(BrowserSourceProviderKind provider)
    {
        return Providers.ContainsKey(provider);
    }

    public static IVideoFrameSource? CreateFrameSource(BrowserScreenProfile screen, BrowserMediaEngine enginePreference)
    {
        return Providers.TryGetValue(screen.ProviderKind, out var provider)
            ? provider.CreateFrameSource(screen, enginePreference)
            : null;
    }

    public static string BuildFrameSourceSignature(BrowserScreenProfile screen, BrowserMediaEngine enginePreference)
    {
        if (!Providers.TryGetValue(screen.ProviderKind, out var provider))
            return string.Join('|', ScreenSourceKind.YouTubeBrowser, screen.ProviderKind);

        var dimensions = provider.GetDimensions(screen);
        return string.Join('|',
            ScreenSourceKind.YouTubeBrowser,
            screen.ProviderKind,
            provider.GetUrl(screen),
            dimensions.Width,
            dimensions.Height,
            enginePreference);
    }

    public static BrowserSourceDimensions GetDimensions(BrowserScreenProfile screen)
    {
        return Providers.TryGetValue(screen.ProviderKind, out var provider)
            ? provider.GetDimensions(screen)
            : new BrowserSourceDimensions(1280, 720);
    }

    public static BrowserSourceRuntimeSettings GetRuntimeSettings(BrowserScreenProfile screen)
    {
        return Providers.TryGetValue(screen.ProviderKind, out var provider)
            ? provider.GetRuntimeSettings(screen)
            : new BrowserSourceRuntimeSettings(false, 0.0f, 1.0f, false, true);
    }

    public static float GetDetectedVideoFps(IVideoFrameSource? frameSource)
    {
        return frameSource is IBrowserFrameSourceRuntime source ? source.DetectedVideoFps : 0.0f;
    }

    public static void ApplyCaptureFps(IVideoFrameSource? frameSource, BrowserScreenProfile screen)
    {
        if (Providers.TryGetValue(screen.ProviderKind, out var provider))
            provider.ApplyCaptureFps(frameSource, screen);
    }

    private static void ApplyCaptureFps(bool manual, float configuredFps, float detectedFps, Action<float> update)
    {
        if (manual)
        {
            update(configuredFps);
            return;
        }

        if (detectedFps > 0.0f)
            update(detectedFps);
    }

    private interface IBrowserSourceProvider
    {
        IVideoFrameSource CreateFrameSource(BrowserScreenProfile screen, BrowserMediaEngine enginePreference);
        string GetUrl(BrowserScreenProfile screen);
        BrowserSourceDimensions GetDimensions(BrowserScreenProfile screen);
        BrowserSourceRuntimeSettings GetRuntimeSettings(BrowserScreenProfile screen);
        void ApplyCaptureFps(IVideoFrameSource? frameSource, BrowserScreenProfile screen);
    }

    private sealed class YouTubeBrowserSourceProvider : IBrowserSourceProvider
    {
        public IVideoFrameSource CreateFrameSource(BrowserScreenProfile screen, BrowserMediaEngine enginePreference)
        {
            return new BrowserFrameSource(
                BrowserSourceDescriptors.YouTube,
                screen.YouTubeUrl,
                screen.YouTubeBrowserWidth,
                screen.YouTubeBrowserHeight,
                screen.YouTubeCaptureFps,
                enginePreference,
                screen.YouTubeAutoplay,
                screen.LoopYouTube,
                screen.YouTubePlaylistAutoplayNext,
                screen.YouTubeAudioEnabled,
                screen.YouTubeVolume,
                screen.YouTubePlaybackRate);
        }

        public string GetUrl(BrowserScreenProfile screen)
        {
            return screen.YouTubeUrl;
        }

        public BrowserSourceDimensions GetDimensions(BrowserScreenProfile screen)
        {
            return new BrowserSourceDimensions(screen.YouTubeBrowserWidth, screen.YouTubeBrowserHeight);
        }

        public BrowserSourceRuntimeSettings GetRuntimeSettings(BrowserScreenProfile screen)
        {
            return new BrowserSourceRuntimeSettings(
                screen.YouTubeAudioEnabled,
                screen.YouTubeVolume,
                screen.YouTubePlaybackRate,
                screen.LoopYouTube,
                screen.YouTubePlaylistAutoplayNext);
        }

        public void ApplyCaptureFps(IVideoFrameSource? frameSource, BrowserScreenProfile screen)
        {
            if (frameSource is not IBrowserFrameSourceRuntime { ProviderKind: BrowserSourceProviderKind.YouTube } source)
                return;

            BrowserSourceProviderRegistry.ApplyCaptureFps(
                screen.YouTubeCaptureFpsManual,
                screen.YouTubeCaptureFps,
                source.DetectedVideoFps,
                source.UpdateCaptureFps);
        }
    }

    private sealed class TwitchBrowserSourceProvider : IBrowserSourceProvider
    {
        public IVideoFrameSource CreateFrameSource(BrowserScreenProfile screen, BrowserMediaEngine enginePreference)
        {
            return new BrowserFrameSource(
                BrowserSourceDescriptors.Twitch,
                screen.TwitchUrl,
                screen.TwitchBrowserWidth,
                screen.TwitchBrowserHeight,
                screen.TwitchCaptureFps,
                enginePreference,
                screen.TwitchAutoplay,
                false,
                false,
                screen.TwitchAudioEnabled,
                screen.TwitchVolume,
                1.0f);
        }

        public string GetUrl(BrowserScreenProfile screen)
        {
            return screen.TwitchUrl;
        }

        public BrowserSourceDimensions GetDimensions(BrowserScreenProfile screen)
        {
            return new BrowserSourceDimensions(screen.TwitchBrowserWidth, screen.TwitchBrowserHeight);
        }

        public BrowserSourceRuntimeSettings GetRuntimeSettings(BrowserScreenProfile screen)
        {
            return new BrowserSourceRuntimeSettings(
                screen.TwitchAudioEnabled,
                screen.TwitchVolume,
                1.0f,
                false,
                true);
        }

        public void ApplyCaptureFps(IVideoFrameSource? frameSource, BrowserScreenProfile screen)
        {
            if (frameSource is not IBrowserFrameSourceRuntime { ProviderKind: BrowserSourceProviderKind.Twitch } source)
                return;

            BrowserSourceProviderRegistry.ApplyCaptureFps(
                screen.TwitchCaptureFpsManual,
                screen.TwitchCaptureFps,
                source.DetectedVideoFps,
                source.UpdateCaptureFps);
        }
    }

    private sealed class DailymotionBrowserSourceProvider : IBrowserSourceProvider
    {
        public IVideoFrameSource CreateFrameSource(BrowserScreenProfile screen, BrowserMediaEngine enginePreference)
        {
            return new BrowserFrameSource(
                BrowserSourceDescriptors.Dailymotion,
                screen.DailymotionUrl,
                screen.DailymotionBrowserWidth,
                screen.DailymotionBrowserHeight,
                screen.DailymotionCaptureFps,
                enginePreference,
                screen.DailymotionAutoplay,
                screen.LoopDailymotion,
                true,
                screen.DailymotionAudioEnabled,
                screen.DailymotionVolume,
                1.0f);
        }

        public string GetUrl(BrowserScreenProfile screen)
        {
            return screen.DailymotionUrl;
        }

        public BrowserSourceDimensions GetDimensions(BrowserScreenProfile screen)
        {
            return new BrowserSourceDimensions(screen.DailymotionBrowserWidth, screen.DailymotionBrowserHeight);
        }

        public BrowserSourceRuntimeSettings GetRuntimeSettings(BrowserScreenProfile screen)
        {
            return new BrowserSourceRuntimeSettings(
                screen.DailymotionAudioEnabled,
                screen.DailymotionVolume,
                1.0f,
                screen.LoopDailymotion,
                true);
        }

        public void ApplyCaptureFps(IVideoFrameSource? frameSource, BrowserScreenProfile screen)
        {
            if (frameSource is not IBrowserFrameSourceRuntime { ProviderKind: BrowserSourceProviderKind.Dailymotion } source)
                return;

            BrowserSourceProviderRegistry.ApplyCaptureFps(
                screen.DailymotionCaptureFpsManual,
                screen.DailymotionCaptureFps,
                source.DetectedVideoFps,
                source.UpdateCaptureFps);
        }
    }
}

internal readonly record struct BrowserSourceDimensions(int Width, int Height);

internal readonly record struct BrowserSourceRuntimeSettings(
    bool AudioEnabled,
    float Volume,
    float PlaybackRate,
    bool Loop,
    bool PlaylistAutoplayNext);
