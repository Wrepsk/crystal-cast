using CrystalCast.Sources.Providers;

namespace CrystalCast.Sources;

/// <summary>
/// Reads and writes one provider's settings on a <see cref="BrowserScreenProfile"/>. The profile stores every
/// provider's settings side by side (<c>YouTubeUrl</c>, <c>TwitchUrl</c>, ...), so that switching provider keeps
/// each one's values; this is the one place that maps a provider to its fields.
/// </summary>
internal sealed class BrowserProviderSettings
{
    private static readonly IReadOnlyDictionary<BrowserSourceProviderKind, BrowserProviderSettings> ByProvider =
        CreateAll().ToDictionary(settings => settings.ProviderKind);

    public required BrowserSourceProviderKind ProviderKind { get; init; }
    public required Func<BrowserScreenProfile, string> GetUrl { get; init; }
    public required Action<BrowserScreenProfile, string> SetUrl { get; init; }
    public required Func<BrowserScreenProfile, int> GetBrowserWidth { get; init; }
    public required Action<BrowserScreenProfile, int> SetBrowserWidth { get; init; }
    public required Func<BrowserScreenProfile, int> GetBrowserHeight { get; init; }
    public required Action<BrowserScreenProfile, int> SetBrowserHeight { get; init; }
    public required Func<BrowserScreenProfile, float> GetCaptureFps { get; init; }
    public required Action<BrowserScreenProfile, float> SetCaptureFps { get; init; }
    public required Func<BrowserScreenProfile, bool> GetCaptureFpsManual { get; init; }
    public required Action<BrowserScreenProfile, bool> SetCaptureFpsManual { get; init; }
    public required Func<BrowserScreenProfile, bool> GetAutoplay { get; init; }
    public required Action<BrowserScreenProfile, bool> SetAutoplay { get; init; }
    public required Func<BrowserScreenProfile, bool> GetAudioEnabled { get; init; }
    public required Action<BrowserScreenProfile, bool> SetAudioEnabled { get; init; }
    public required Func<BrowserScreenProfile, float> GetVolume { get; init; }
    public required Action<BrowserScreenProfile, float> SetVolume { get; init; }

    /// <summary>Providers without a loop setting read as off and ignore writes.</summary>
    public Func<BrowserScreenProfile, bool> GetLoop { get; init; } = _ => false;
    public Action<BrowserScreenProfile, bool> SetLoop { get; init; } = (_, _) => { };

    /// <summary>Providers without this setting read as on and ignore writes.</summary>
    public Func<BrowserScreenProfile, bool> GetPlaylistAutoplayNext { get; init; } = _ => true;
    public Action<BrowserScreenProfile, bool> SetPlaylistAutoplayNext { get; init; } = (_, _) => { };

    /// <summary>False when the provider has no playback-rate setting; it then reads as 1.0 and ignores writes.</summary>
    public bool HasPlaybackRate { get; init; }
    public Func<BrowserScreenProfile, float> GetPlaybackRate { get; init; } = _ => 1.0f;
    public Action<BrowserScreenProfile, float> SetPlaybackRate { get; init; } = (_, _) => { };

    public static IEnumerable<BrowserProviderSettings> All => ByProvider.Values;

    public static bool IsSupported(BrowserSourceProviderKind provider) => ByProvider.ContainsKey(provider);

    /// <summary>The settings of <paramref name="provider"/>; an unknown provider reads as YouTube.</summary>
    public static BrowserProviderSettings For(BrowserSourceProviderKind provider)
    {
        return ByProvider.TryGetValue(provider, out var settings)
            ? settings
            : ByProvider[BrowserSourceProviderKind.YouTube];
    }

    /// <summary>The settings of the provider <paramref name="screen"/> is showing.</summary>
    public static BrowserProviderSettings For(BrowserScreenProfile screen) => For(screen.ProviderKind);

    private static BrowserProviderSettings[] CreateAll()
    {
        return
        [
            new BrowserProviderSettings
            {
                ProviderKind = BrowserSourceProviderKind.YouTube,
                GetUrl = screen => screen.YouTubeUrl,
                SetUrl = (screen, value) => screen.YouTubeUrl = value,
                GetBrowserWidth = screen => screen.YouTubeBrowserWidth,
                SetBrowserWidth = (screen, value) => screen.YouTubeBrowserWidth = value,
                GetBrowserHeight = screen => screen.YouTubeBrowserHeight,
                SetBrowserHeight = (screen, value) => screen.YouTubeBrowserHeight = value,
                GetCaptureFps = screen => screen.YouTubeCaptureFps,
                SetCaptureFps = (screen, value) => screen.YouTubeCaptureFps = value,
                GetCaptureFpsManual = screen => screen.YouTubeCaptureFpsManual,
                SetCaptureFpsManual = (screen, value) => screen.YouTubeCaptureFpsManual = value,
                GetAutoplay = screen => screen.YouTubeAutoplay,
                SetAutoplay = (screen, value) => screen.YouTubeAutoplay = value,
                GetAudioEnabled = screen => screen.YouTubeAudioEnabled,
                SetAudioEnabled = (screen, value) => screen.YouTubeAudioEnabled = value,
                GetVolume = screen => screen.YouTubeVolume,
                SetVolume = (screen, value) => screen.YouTubeVolume = value,
                GetLoop = screen => screen.LoopYouTube,
                SetLoop = (screen, value) => screen.LoopYouTube = value,
                GetPlaylistAutoplayNext = screen => screen.YouTubePlaylistAutoplayNext,
                SetPlaylistAutoplayNext = (screen, value) => screen.YouTubePlaylistAutoplayNext = value,
                HasPlaybackRate = true,
                GetPlaybackRate = screen => screen.YouTubePlaybackRate,
                SetPlaybackRate = (screen, value) => screen.YouTubePlaybackRate = value,
            },
            new BrowserProviderSettings
            {
                ProviderKind = BrowserSourceProviderKind.Twitch,
                GetUrl = screen => screen.TwitchUrl,
                SetUrl = (screen, value) => screen.TwitchUrl = value,
                GetBrowserWidth = screen => screen.TwitchBrowserWidth,
                SetBrowserWidth = (screen, value) => screen.TwitchBrowserWidth = value,
                GetBrowserHeight = screen => screen.TwitchBrowserHeight,
                SetBrowserHeight = (screen, value) => screen.TwitchBrowserHeight = value,
                GetCaptureFps = screen => screen.TwitchCaptureFps,
                SetCaptureFps = (screen, value) => screen.TwitchCaptureFps = value,
                GetCaptureFpsManual = screen => screen.TwitchCaptureFpsManual,
                SetCaptureFpsManual = (screen, value) => screen.TwitchCaptureFpsManual = value,
                GetAutoplay = screen => screen.TwitchAutoplay,
                SetAutoplay = (screen, value) => screen.TwitchAutoplay = value,
                GetAudioEnabled = screen => screen.TwitchAudioEnabled,
                SetAudioEnabled = (screen, value) => screen.TwitchAudioEnabled = value,
                GetVolume = screen => screen.TwitchVolume,
                SetVolume = (screen, value) => screen.TwitchVolume = value,
            },
            new BrowserProviderSettings
            {
                ProviderKind = BrowserSourceProviderKind.Dailymotion,
                GetUrl = screen => screen.DailymotionUrl,
                SetUrl = (screen, value) => screen.DailymotionUrl = value,
                GetBrowserWidth = screen => screen.DailymotionBrowserWidth,
                SetBrowserWidth = (screen, value) => screen.DailymotionBrowserWidth = value,
                GetBrowserHeight = screen => screen.DailymotionBrowserHeight,
                SetBrowserHeight = (screen, value) => screen.DailymotionBrowserHeight = value,
                GetCaptureFps = screen => screen.DailymotionCaptureFps,
                SetCaptureFps = (screen, value) => screen.DailymotionCaptureFps = value,
                GetCaptureFpsManual = screen => screen.DailymotionCaptureFpsManual,
                SetCaptureFpsManual = (screen, value) => screen.DailymotionCaptureFpsManual = value,
                GetAutoplay = screen => screen.DailymotionAutoplay,
                SetAutoplay = (screen, value) => screen.DailymotionAutoplay = value,
                GetAudioEnabled = screen => screen.DailymotionAudioEnabled,
                SetAudioEnabled = (screen, value) => screen.DailymotionAudioEnabled = value,
                GetVolume = screen => screen.DailymotionVolume,
                SetVolume = (screen, value) => screen.DailymotionVolume = value,
                GetLoop = screen => screen.LoopDailymotion,
                SetLoop = (screen, value) => screen.LoopDailymotion = value,
            },
            new BrowserProviderSettings
            {
                ProviderKind = BrowserSourceProviderKind.Vimeo,
                GetUrl = screen => screen.VimeoUrl,
                SetUrl = (screen, value) => screen.VimeoUrl = value,
                GetBrowserWidth = screen => screen.VimeoBrowserWidth,
                SetBrowserWidth = (screen, value) => screen.VimeoBrowserWidth = value,
                GetBrowserHeight = screen => screen.VimeoBrowserHeight,
                SetBrowserHeight = (screen, value) => screen.VimeoBrowserHeight = value,
                GetCaptureFps = screen => screen.VimeoCaptureFps,
                SetCaptureFps = (screen, value) => screen.VimeoCaptureFps = value,
                GetCaptureFpsManual = screen => screen.VimeoCaptureFpsManual,
                SetCaptureFpsManual = (screen, value) => screen.VimeoCaptureFpsManual = value,
                GetAutoplay = screen => screen.VimeoAutoplay,
                SetAutoplay = (screen, value) => screen.VimeoAutoplay = value,
                GetAudioEnabled = screen => screen.VimeoAudioEnabled,
                SetAudioEnabled = (screen, value) => screen.VimeoAudioEnabled = value,
                GetVolume = screen => screen.VimeoVolume,
                SetVolume = (screen, value) => screen.VimeoVolume = value,
                GetLoop = screen => screen.LoopVimeo,
                SetLoop = (screen, value) => screen.LoopVimeo = value,
                HasPlaybackRate = true,
                GetPlaybackRate = screen => screen.VimeoPlaybackRate,
                SetPlaybackRate = (screen, value) => screen.VimeoPlaybackRate = value,
            },
            new BrowserProviderSettings
            {
                ProviderKind = BrowserSourceProviderKind.GenericWeb,
                GetUrl = screen => screen.GenericWebUrl,
                SetUrl = (screen, value) => screen.GenericWebUrl = value,
                GetBrowserWidth = screen => screen.GenericWebBrowserWidth,
                SetBrowserWidth = (screen, value) => screen.GenericWebBrowserWidth = value,
                GetBrowserHeight = screen => screen.GenericWebBrowserHeight,
                SetBrowserHeight = (screen, value) => screen.GenericWebBrowserHeight = value,
                GetCaptureFps = screen => screen.GenericWebCaptureFps,
                SetCaptureFps = (screen, value) => screen.GenericWebCaptureFps = value,
                GetCaptureFpsManual = screen => screen.GenericWebCaptureFpsManual,
                SetCaptureFpsManual = (screen, value) => screen.GenericWebCaptureFpsManual = value,
                GetAutoplay = screen => screen.GenericWebAutoplay,
                SetAutoplay = (screen, value) => screen.GenericWebAutoplay = value,
                GetAudioEnabled = screen => screen.GenericWebAudioEnabled,
                SetAudioEnabled = (screen, value) => screen.GenericWebAudioEnabled = value,
                GetVolume = screen => screen.GenericWebVolume,
                SetVolume = (screen, value) => screen.GenericWebVolume = value,
                GetLoop = screen => screen.LoopGenericWeb,
                SetLoop = (screen, value) => screen.LoopGenericWeb = value,
                HasPlaybackRate = true,
                GetPlaybackRate = screen => screen.GenericWebPlaybackRate,
                SetPlaybackRate = (screen, value) => screen.GenericWebPlaybackRate = value,
            },
        ];
    }
}
