using Dalamud.Configuration;
using CrystalCast.Playlist;
using CrystalCast.Rendering;
using CrystalCast.Sources;
using CrystalCast.Sources.Providers;
using System;

namespace CrystalCast;

[Serializable]
public class Configuration : IPluginConfiguration
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(300);
    [NonSerialized] private ConfigurationSaveCoordinator? saveCoordinator;

    public const int MaxBrowserScreens = 8;
    public const int MaxIpcBrowserScreens = 56;
    public const int MaxRenderableBrowserScreens = MaxBrowserScreens + MaxIpcBrowserScreens;
    public const int MaxActiveBrowserScreens = 8;

    public int Version { get; set; } = 3;

    public bool Enabled { get; set; } = true;
    public ScreenSourceKind SourceKind { get; set; } = ScreenSourceKind.Browser;

    public string ScreenId { get; set; } = Guid.NewGuid().ToString("N");
    public long LocalSequence { get; set; }
    public bool IpcEnabled { get; set; } = true;

    /// <summary>Address of a self-hosted sync rooms relay (see relay/). Empty uses the CrystalCast relay.</summary>
    public string RoomRelayUrl { get; set; } = string.Empty;

    // Version 1 compatibility fields. They are read only when creating the first browser screen.
    public ScreenPlacementMode LocalVideoPlacementMode { get; set; } = ScreenPlacementMode.World;
    public float PositionX { get; set; }
    public float PositionY { get; set; } = 1.6f;
    public float PositionZ { get; set; } = 3.0f;
    public float YawRadians { get; set; }
    public float PitchRadians { get; set; }
    public float RollRadians { get; set; }
    public float WidthMeters { get; set; } = 3.0f;
    public float HeightMeters { get; set; } = 1.6875f;
    public float ScreenCurveAmountMeters { get; set; }

    public float OccludedAlpha { get; set; } = 0.0f;
    public float OcclusionTolerance { get; set; } = 0.02f;
    public bool EnableDistanceFade { get; set; }
    public float FadeStartMeters { get; set; } = 35.0f;
    public float FadeStopMeters { get; set; } = 60.0f;
    public ScreenOutputMode OutputMode { get; set; } = DefaultOutputMode;

    /// <summary>Scene composite only: screens stay visible when the game UI is hidden. See HiddenUiPolicy.</summary>
    public bool KeepScreensWhenUiHidden { get; set; } = true;

    /// <summary>Scene composite only: screens stay visible in GPose, so they show up in screenshots.</summary>
    public bool KeepScreensInGpose { get; set; } = true;

    /// <summary>
    /// Wine used to default to native overlay; set once a Wine configuration has been moved to scene
    /// composite, so a later native overlay choice sticks.
    /// </summary>
    public bool WineSceneCompositeDefaultApplied { get; set; }
    public bool ShowDebugMarker { get; set; }
    public bool EnableGpuDiagnostics { get; set; }
    public ScreenPlacementGizmoOperation PlacementGizmoOperation { get; set; } = ScreenPlacementGizmoOperation.Translate;

    /// <summary>Move and rotate along the world's axes instead of the screen's own.</summary>
    public bool PlacementGizmoWorldAxes { get; set; }
    public bool WineWebView2SetupDismissed { get; set; }
    public bool FirstRunGuideCompleted { get; set; }

    public bool SpatialAudioEnabled { get; set; } = true;
    public float SpatialAudioFullVolumeRadiusMeters { get; set; } = 4.0f;
    public float SpatialAudioSilentRadiusMeters { get; set; } = 18.0f;

    public string YouTubeUrl { get; set; } = string.Empty;
    public int YouTubeBrowserWidth { get; set; } = 1280;
    public int YouTubeBrowserHeight { get; set; } = 720;
    public float YouTubeCaptureFps { get; set; } = 60.0f;
    public BrowserMediaEngine YouTubeBrowserEngine { get; set; } = BrowserMediaEngine.Auto;
    public JpegCaptureResolution WebView2JpegCaptureResolution { get; set; } = JpegCaptureResolution.Full1080p;
    public bool YouTubeAutoplay { get; set; } = true;
    public bool LoopYouTube { get; set; }
    public bool YouTubeAudioEnabled { get; set; } = true;
    public float YouTubeVolume { get; set; } = 0.7f;
    public float YouTubePlaybackRate { get; set; } = 1.0f;

    public bool PlaybackPaused { get; set; }

    public List<BrowserScreenProfile> BrowserScreens { get; set; } = [];
    public bool BrowserScreensInitialized { get; set; }
    public string ActiveBrowserScreenId { get; set; } = string.Empty;
    public List<ScreenPlacementPreset> PlacementPresets { get; set; } = [];
    public string ActivePlacementPresetId { get; set; } = string.Empty;

    public static ScreenOutputMode DefaultOutputMode => Rendering.RenderOutputPolicy.GetDefault();

    public bool Normalize()
    {
        return ConfigurationMigration.Normalize(this);
    }

    public BrowserScreenProfile? GetActiveBrowserScreen()
    {
        Normalize();
        return BrowserScreens.FirstOrDefault(screen => screen.ScreenId == ActiveBrowserScreenId)
            ?? BrowserScreens.FirstOrDefault();
    }

    /// <summary>The screen with this ID, or null. Does not normalize the configuration first.</summary>
    public BrowserScreenProfile? FindBrowserScreen(string? screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }

    public BrowserScreenProfile CreateDefaultBrowserScreen(string name, bool createdByIpc = false)
    {
        var screen = new BrowserScreenProfile
        {
            Name = name,
            CreatedByIpc = createdByIpc,
            Placement = new ScreenPlacementSettings(),
            SpatialAudioEnabled = createdByIpc,
            YouTubeAudioEnabled = true,
            TwitchAudioEnabled = true,
            DailymotionAudioEnabled = true,
            VimeoAudioEnabled = true,
            GenericWebAudioEnabled = true,
        };
        screen.Normalize(name, new HashSet<string>(StringComparer.Ordinal));
        return screen;
    }

    public ScreenPlacementPreset? GetActivePlacementPreset()
    {
        Normalize();
        return PlacementPresets.FirstOrDefault(preset => preset.PresetId == ActivePlacementPresetId);
    }

    public ScreenPlacementPreset CreatePlacementPreset(string name, ScreenPlacementSettings placement)
    {
        var preset = new ScreenPlacementPreset
        {
            Name = name,
            Placement = placement.Clone(),
        };
        preset.Normalize(name, new HashSet<string>(StringComparer.Ordinal));
        return preset;
    }

    internal BrowserScreenProfile CreateBrowserScreenFromLegacySettings(string name)
    {
        var screen = new BrowserScreenProfile
        {
            ScreenId = ScreenId,
            Name = name,
            Enabled = Enabled,
            LocalSequence = LocalSequence,
            PlaybackPaused = PlaybackPaused,
            ProviderKind = BrowserSourceProviderKind.YouTube,
            Placement = new ScreenPlacementSettings
            {
                Mode = LocalVideoPlacementMode,
                PositionX = PositionX,
                PositionY = PositionY,
                PositionZ = PositionZ,
                YawRadians = YawRadians,
                PitchRadians = PitchRadians,
                RollRadians = RollRadians,
                WidthMeters = WidthMeters,
                HeightMeters = HeightMeters,
                ScreenCurveAmountMeters = ScreenCurveAmountMeters,
                OccludedAlpha = OccludedAlpha,
                OcclusionTolerance = OcclusionTolerance,
                EnableDistanceFade = EnableDistanceFade,
                FadeStartMeters = FadeStartMeters,
                FadeStopMeters = FadeStopMeters,
            },
            YouTubeUrl = YouTubeUrl,
            YouTubeBrowserWidth = YouTubeBrowserWidth,
            YouTubeBrowserHeight = YouTubeBrowserHeight,
            YouTubeCaptureFps = YouTubeCaptureFps,
            YouTubeCaptureFpsManual = Math.Abs(YouTubeCaptureFps - 60.0f) > 0.01f,
            YouTubeAutoplay = YouTubeAutoplay,
            LoopYouTube = LoopYouTube,
            YouTubeAudioEnabled = YouTubeAudioEnabled,
            YouTubeVolume = YouTubeVolume,
            YouTubePlaybackRate = YouTubePlaybackRate,
            TwitchAudioEnabled = true,
            DailymotionAudioEnabled = true,
            VimeoAudioEnabled = true,
            GenericWebAudioEnabled = true,
            SpatialAudioEnabled = SpatialAudioEnabled,
            SpatialAudioFullVolumeRadiusMeters = SpatialAudioFullVolumeRadiusMeters,
            SpatialAudioSilentRadiusMeters = SpatialAudioSilentRadiusMeters,
        };
        screen.Normalize(name, new HashSet<string>(StringComparer.Ordinal));
        return screen;
    }

    internal void AttachPersistence(Action<Configuration> persist)
    {
        saveCoordinator = new ConfigurationSaveCoordinator(() => persist(this), SaveDebounce);
    }

    public void Save() => saveCoordinator?.Request();

    internal void ProcessPendingSave() => saveCoordinator?.Process();

    internal void FlushPendingSave() => saveCoordinator?.Flush();
}

public enum BrowserSourceProviderKind
{
    YouTube = 1,
    Twitch = 2,
    Dailymotion = 3,
    Vimeo = 4,
    GenericWeb = 5,
}

public enum ScreenPlacementMode
{
    World = 0,
    FollowPlayer = 1,
    FollowCamera = 2,
}

public enum ScreenPlacementGizmoOperation
{
    Translate = 0,
    Rotate = 1,

    /// <summary>Drag the width handle; the height follows the video's shape.</summary>
    Resize = 2,
}

[Serializable]
public sealed class ScreenPlacementPreset
{
    public string PresetId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Placement";
    public ScreenPlacementSettings Placement { get; set; } = new();

    public bool Normalize(string defaultName, ISet<string> usedPresetIds)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(PresetId) || !usedPresetIds.Add(PresetId))
        {
            PresetId = Guid.NewGuid().ToString("N");
            usedPresetIds.Add(PresetId);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = defaultName;
            changed = true;
        }

        if (Placement == null)
        {
            Placement = new ScreenPlacementSettings();
            changed = true;
        }

        changed |= Placement.Normalize();
        return changed;
    }
}

[Serializable]
public sealed class BrowserScreenProfile
{
    public string ScreenId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Browser screen";
    public bool Enabled { get; set; } = true;
    public bool CreatedByIpc { get; set; }

    /// <summary>
    /// True when an IPC call set <see cref="GenericWebUrl"/> on this screen, even one the user created.
    /// Such pages need the user's approval like IPC-created screens, until the user types an address.
    /// </summary>
    public bool GenericWebUrlFromIpc { get; set; }

    /// <summary>Whether this screen's Generic Web page must be approved before it loads.</summary>
    internal bool RequiresGenericWebIpcApproval =>
        ProviderKind == BrowserSourceProviderKind.GenericWeb && (CreatedByIpc || GenericWebUrlFromIpc);
    public string IpcOwnerId { get; set; } = string.Empty;
    public bool SourceControlsLocked { get; set; }
    public string SourceControlsOwnerId { get; set; } = string.Empty;
    public long LocalSequence { get; set; }
    public ScreenPlacementSettings Placement { get; set; } = new();
    public bool PlaybackPaused { get; set; }
    public BrowserSourceProviderKind ProviderKind { get; set; } = BrowserSourceProviderKind.YouTube;

    public string YouTubeUrl { get; set; } = string.Empty;
    public int YouTubeBrowserWidth { get; set; } = 1280;
    public int YouTubeBrowserHeight { get; set; } = 720;
    public float YouTubeCaptureFps { get; set; } = 60.0f;
    public bool YouTubeCaptureFpsManual { get; set; }
    public bool YouTubeAutoplay { get; set; } = true;
    public bool LoopYouTube { get; set; }
    public bool YouTubePlaylistAutoplayNext { get; set; } = true;
    public bool YouTubeAudioEnabled { get; set; }
    public float YouTubeVolume { get; set; } = 0.7f;
    public float YouTubePlaybackRate { get; set; } = 1.0f;

    public string TwitchUrl { get; set; } = string.Empty;
    public int TwitchBrowserWidth { get; set; } = 1920;
    public int TwitchBrowserHeight { get; set; } = 1080;
    public float TwitchCaptureFps { get; set; } = 60.0f;
    public bool TwitchCaptureFpsManual { get; set; }
    public bool TwitchAutoplay { get; set; } = true;
    public bool TwitchAudioEnabled { get; set; }
    public float TwitchVolume { get; set; } = 0.7f;

    public string DailymotionUrl { get; set; } = string.Empty;
    public int DailymotionBrowserWidth { get; set; } = 1280;
    public int DailymotionBrowserHeight { get; set; } = 720;
    public float DailymotionCaptureFps { get; set; } = 60.0f;
    public bool DailymotionCaptureFpsManual { get; set; }
    public bool DailymotionAutoplay { get; set; } = true;
    public bool LoopDailymotion { get; set; }
    public bool DailymotionAudioEnabled { get; set; }
    public float DailymotionVolume { get; set; } = 0.7f;

    public string VimeoUrl { get; set; } = string.Empty;
    public int VimeoBrowserWidth { get; set; } = 1280;
    public int VimeoBrowserHeight { get; set; } = 720;
    public float VimeoCaptureFps { get; set; } = 60.0f;
    public bool VimeoCaptureFpsManual { get; set; }
    public bool VimeoAutoplay { get; set; } = true;
    public bool LoopVimeo { get; set; }
    public bool VimeoAudioEnabled { get; set; }
    public float VimeoVolume { get; set; } = 0.7f;
    public float VimeoPlaybackRate { get; set; } = 1.0f;

    public string GenericWebUrl { get; set; } = string.Empty;
    public int GenericWebBrowserWidth { get; set; } = 1280;
    public int GenericWebBrowserHeight { get; set; } = 720;
    public float GenericWebCaptureFps { get; set; } = 60.0f;
    public bool GenericWebCaptureFpsManual { get; set; }
    public bool GenericWebAutoplay { get; set; } = true;
    public bool LoopGenericWeb { get; set; }
    public bool GenericWebAudioEnabled { get; set; }
    public float GenericWebVolume { get; set; } = 0.7f;
    public float GenericWebPlaybackRate { get; set; } = 1.0f;

    public bool SpatialAudioEnabled { get; set; }
    public float SpatialAudioFullVolumeRadiusMeters { get; set; } = 4.0f;
    public float SpatialAudioSilentRadiusMeters { get; set; } = 18.0f;

    /// <summary>Videos that play after the current one ends.</summary>
    public List<PlaylistEntry> Playlist { get; set; } = [];

    /// <summary>The playlist entry now loaded on the screen, or empty when the source didn't come from the playlist.</summary>
    public string PlaylistCurrentEntryId { get; set; } = string.Empty;
    public bool PlaylistShuffle { get; set; }
    public bool PlaylistLoop { get; set; }

    /// <summary>
    /// Lets other plugins add videos through IPC. Only the user can change it: no IPC call sets it.
    /// </summary>
    public bool PlaylistAllowIpcAdds { get; set; }

    /// <summary>
    /// On a screen a plugin created: whether the user may add videos. The rest of that screen's playlist is
    /// always the plugin's.
    /// Only the plugin sets it, through <c>Screen.Create</c> or <c>Screen.Update</c>. Ignored on the user's screens.
    /// </summary>
    public bool PlaylistAllowUserAdds { get; set; }

    public bool Normalize(string defaultName, ISet<string> usedScreenIds)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(ScreenId) || !usedScreenIds.Add(ScreenId))
        {
            ScreenId = Guid.NewGuid().ToString("N");
            usedScreenIds.Add(ScreenId);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = defaultName;
            changed = true;
        }

        IpcOwnerId ??= string.Empty;
        SourceControlsOwnerId ??= string.Empty;

        if (Placement == null)
        {
            Placement = new ScreenPlacementSettings();
            changed = true;
        }

        if (!BrowserSourceProviderRegistry.IsSupported(ProviderKind))
        {
            ProviderKind = BrowserSourceProviderKind.YouTube;
            changed = true;
        }

        changed |= BrowserSourceProviderRegistry.NormalizeProviderSettings(this);

        if (SpatialAudioSilentRadiusMeters <= SpatialAudioFullVolumeRadiusMeters)
        {
            SpatialAudioSilentRadiusMeters = SpatialAudioFullVolumeRadiusMeters + 0.1f;
            changed = true;
        }

        changed |= Placement.Normalize();
        changed |= ScreenPlaylistPolicy.Normalize(this);
        return changed;
    }

    public BrowserScreenProfile CloneAsNew(string name)
    {
        return new BrowserScreenProfile
        {
            ScreenId = Guid.NewGuid().ToString("N"),
            Name = name,
            Enabled = Enabled,
            CreatedByIpc = false,
            IpcOwnerId = string.Empty,
            SourceControlsLocked = false,
            SourceControlsOwnerId = string.Empty,
            LocalSequence = 0,
            Placement = Placement.Clone(),
            PlaybackPaused = PlaybackPaused,
            ProviderKind = ProviderKind,
            YouTubeUrl = YouTubeUrl,
            YouTubeBrowserWidth = YouTubeBrowserWidth,
            YouTubeBrowserHeight = YouTubeBrowserHeight,
            YouTubeCaptureFps = YouTubeCaptureFps,
            YouTubeCaptureFpsManual = YouTubeCaptureFpsManual,
            YouTubeAutoplay = YouTubeAutoplay,
            LoopYouTube = LoopYouTube,
            YouTubePlaylistAutoplayNext = YouTubePlaylistAutoplayNext,
            YouTubeAudioEnabled = YouTubeAudioEnabled,
            YouTubeVolume = YouTubeVolume,
            YouTubePlaybackRate = YouTubePlaybackRate,
            TwitchUrl = TwitchUrl,
            TwitchBrowserWidth = TwitchBrowserWidth,
            TwitchBrowserHeight = TwitchBrowserHeight,
            TwitchCaptureFps = TwitchCaptureFps,
            TwitchCaptureFpsManual = TwitchCaptureFpsManual,
            TwitchAutoplay = TwitchAutoplay,
            TwitchAudioEnabled = TwitchAudioEnabled,
            TwitchVolume = TwitchVolume,
            DailymotionUrl = DailymotionUrl,
            DailymotionBrowserWidth = DailymotionBrowserWidth,
            DailymotionBrowserHeight = DailymotionBrowserHeight,
            DailymotionCaptureFps = DailymotionCaptureFps,
            DailymotionCaptureFpsManual = DailymotionCaptureFpsManual,
            DailymotionAutoplay = DailymotionAutoplay,
            LoopDailymotion = LoopDailymotion,
            DailymotionAudioEnabled = DailymotionAudioEnabled,
            DailymotionVolume = DailymotionVolume,
            VimeoUrl = VimeoUrl,
            VimeoBrowserWidth = VimeoBrowserWidth,
            VimeoBrowserHeight = VimeoBrowserHeight,
            VimeoCaptureFps = VimeoCaptureFps,
            VimeoCaptureFpsManual = VimeoCaptureFpsManual,
            VimeoAutoplay = VimeoAutoplay,
            LoopVimeo = LoopVimeo,
            VimeoAudioEnabled = VimeoAudioEnabled,
            VimeoVolume = VimeoVolume,
            VimeoPlaybackRate = VimeoPlaybackRate,
            GenericWebUrl = GenericWebUrl,
            GenericWebUrlFromIpc = GenericWebUrlFromIpc,
            GenericWebBrowserWidth = GenericWebBrowserWidth,
            GenericWebBrowserHeight = GenericWebBrowserHeight,
            GenericWebCaptureFps = GenericWebCaptureFps,
            GenericWebCaptureFpsManual = GenericWebCaptureFpsManual,
            GenericWebAutoplay = GenericWebAutoplay,
            LoopGenericWeb = LoopGenericWeb,
            GenericWebAudioEnabled = GenericWebAudioEnabled,
            GenericWebVolume = GenericWebVolume,
            GenericWebPlaybackRate = GenericWebPlaybackRate,
            SpatialAudioEnabled = SpatialAudioEnabled,
            SpatialAudioFullVolumeRadiusMeters = SpatialAudioFullVolumeRadiusMeters,
            SpatialAudioSilentRadiusMeters = SpatialAudioSilentRadiusMeters,
            // A copied screen keeps the videos and play order, but not the permission for other plugins to add.
            Playlist = Playlist.Select(entry => entry.CloneAsNew()).ToList(),
            PlaylistShuffle = PlaylistShuffle,
            PlaylistLoop = PlaylistLoop,
        };
    }

    /// <summary>An exact copy of this screen. IPC updates replace the screen with one, so it must keep everything.</summary>
    public BrowserScreenProfile Clone()
    {
        var clone = CloneAsNew(Name);
        clone.ScreenId = ScreenId;
        clone.CreatedByIpc = CreatedByIpc;
        clone.IpcOwnerId = IpcOwnerId;
        clone.SourceControlsLocked = SourceControlsLocked;
        clone.SourceControlsOwnerId = SourceControlsOwnerId;
        clone.LocalSequence = LocalSequence;
        clone.Playlist = Playlist.Select(entry => entry.Clone()).ToList();
        clone.PlaylistCurrentEntryId = PlaylistCurrentEntryId;
        clone.PlaylistAllowIpcAdds = PlaylistAllowIpcAdds;
        clone.PlaylistAllowUserAdds = PlaylistAllowUserAdds;
        return clone;
    }
}

[Serializable]
public sealed class PlaylistEntry
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
    public BrowserSourceProviderKind ProviderKind { get; set; } = BrowserSourceProviderKind.YouTube;
    public string Url { get; set; } = string.Empty;

    /// <summary>A name the adding plugin gave. Empty when the site's own title should be used.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>What the link points at, such as the video ID or page address. Shown until the title is known.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>The video's title, from the site or from playing it.</summary>
    public string MetadataTitle { get; set; } = string.Empty;

    /// <summary>The channel or uploader.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>An https image on the site's own image host.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }

    /// <summary>The site answered the metadata lookup, so it isn't asked again.</summary>
    public bool MetadataLookedUp { get; set; }

    /// <summary>True when another plugin added this entry through IPC.</summary>
    public bool AddedByIpc { get; set; }

    /// <summary>The owner ID the adding plugin gave. Empty for entries the user added.</summary>
    public string AddedBy { get; set; } = string.Empty;

    /// <summary>Whether this entry has played since the playlist last started over.</summary>
    public bool Played { get; set; }

    public PlaylistEntry Clone()
    {
        return new PlaylistEntry
        {
            EntryId = EntryId,
            ProviderKind = ProviderKind,
            Url = Url,
            Title = Title,
            SourceName = SourceName,
            MetadataTitle = MetadataTitle,
            Author = Author,
            ThumbnailUrl = ThumbnailUrl,
            DurationSeconds = DurationSeconds,
            MetadataLookedUp = MetadataLookedUp,
            AddedByIpc = AddedByIpc,
            AddedBy = AddedBy,
            Played = Played,
        };
    }

    public PlaylistEntry CloneAsNew()
    {
        var clone = Clone();
        clone.EntryId = Guid.NewGuid().ToString("N");
        clone.Played = false;
        return clone;
    }
}

[Serializable]
public sealed class ScreenPlacementSettings
{
    public ScreenPlacementMode Mode { get; set; } = ScreenPlacementMode.World;
    public float PositionX { get; set; }
    public float PositionY { get; set; } = 1.6f;
    public float PositionZ { get; set; } = 3.0f;
    public float YawRadians { get; set; }
    public float PitchRadians { get; set; }
    public float RollRadians { get; set; }
    public float WidthMeters { get; set; } = 3.0f;
    public float HeightMeters { get; set; } = 1.6875f;
    public float ScreenCurveAmountMeters { get; set; }
    public float OccludedAlpha { get; set; } = 0.0f;
    public float OcclusionTolerance { get; set; } = 0.02f;
    public bool EnableDistanceFade { get; set; }
    public float FadeStartMeters { get; set; } = 35.0f;
    public float FadeStopMeters { get; set; } = 60.0f;

    public bool Normalize()
    {
        var changed = false;
        if (Mode is not (ScreenPlacementMode.World or ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera))
        {
            Mode = ScreenPlacementMode.World;
            changed = true;
        }

        if (WidthMeters < 0.1f)
        {
            WidthMeters = 0.1f;
            changed = true;
        }

        if (HeightMeters < 0.1f)
        {
            HeightMeters = 0.1f;
            changed = true;
        }

        var occludedAlpha = Math.Clamp(OccludedAlpha, 0.0f, 1.0f);
        if (Math.Abs(OccludedAlpha - occludedAlpha) > 0.0001f)
        {
            OccludedAlpha = occludedAlpha;
            changed = true;
        }

        var occlusionTolerance = Math.Max(0.0f, OcclusionTolerance);
        if (Math.Abs(OcclusionTolerance - occlusionTolerance) > 0.0001f)
        {
            OcclusionTolerance = occlusionTolerance;
            changed = true;
        }
        if (FadeStopMeters <= FadeStartMeters)
        {
            FadeStopMeters = FadeStartMeters + 0.01f;
            changed = true;
        }

        var maxCurveAmount = Math.Max(0.0f, WidthMeters / MathF.PI);
        if (ScreenCurveAmountMeters > maxCurveAmount)
        {
            ScreenCurveAmountMeters = maxCurveAmount;
            changed = true;
        }
        else if (ScreenCurveAmountMeters < 0.0f)
        {
            ScreenCurveAmountMeters = 0.0f;
            changed = true;
        }

        return changed;
    }

    public ScreenPlacementSettings Clone()
    {
        var clone = new ScreenPlacementSettings();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(ScreenPlacementSettings source)
    {
        Mode = source.Mode;
        PositionX = source.PositionX;
        PositionY = source.PositionY;
        PositionZ = source.PositionZ;
        YawRadians = source.YawRadians;
        PitchRadians = source.PitchRadians;
        RollRadians = source.RollRadians;
        WidthMeters = source.WidthMeters;
        HeightMeters = source.HeightMeters;
        ScreenCurveAmountMeters = source.ScreenCurveAmountMeters;
        OccludedAlpha = source.OccludedAlpha;
        OcclusionTolerance = source.OcclusionTolerance;
        EnableDistanceFade = source.EnableDistanceFade;
        FadeStartMeters = source.FadeStartMeters;
        FadeStopMeters = source.FadeStopMeters;
        Normalize();
    }
}
