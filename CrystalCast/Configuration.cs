using Dalamud.Configuration;
using CrystalCast.Video;
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

    public int Version { get; set; } = 2;

    public bool Enabled { get; set; } = true;
    public ScreenSourceKind SourceKind { get; set; } = ScreenSourceKind.Browser;

    public string ScreenId { get; set; } = Guid.NewGuid().ToString("N");
    public long LocalSequence { get; set; }
    public bool IpcEnabled { get; set; } = true;

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
    public bool ShowDebugMarker { get; set; }
    public bool EnableGpuDiagnostics { get; set; }
    public bool PlacementGizmoEnabled { get; set; }
    public ScreenPlacementGizmoOperation PlacementGizmoOperation { get; set; } = ScreenPlacementGizmoOperation.Translate;

    public bool SpatialAudioEnabled { get; set; } = true;
    public float SpatialAudioFullVolumeRadiusMeters { get; set; } = 4.0f;
    public float SpatialAudioSilentRadiusMeters { get; set; } = 18.0f;

    public string YouTubeUrl { get; set; } = string.Empty;
    public int YouTubeBrowserWidth { get; set; } = 1280;
    public int YouTubeBrowserHeight { get; set; } = 720;
    public float YouTubeCaptureFps { get; set; } = 60.0f;
    public BrowserMediaEngine YouTubeBrowserEngine { get; set; } = BrowserMediaEngine.Auto;
    public bool YouTubeAutoplay { get; set; } = true;
    public bool LoopYouTube { get; set; }
    public bool YouTubeAudioEnabled { get; set; }
    public float YouTubeVolume { get; set; } = 0.7f;
    public float YouTubePlaybackRate { get; set; } = 1.0f;

    public bool PlaybackPaused { get; set; }

    public List<BrowserScreenProfile> BrowserScreens { get; set; } = [];
    public string ActiveBrowserScreenId { get; set; } = string.Empty;
    public List<ScreenPlacementPreset> PlacementPresets { get; set; } = [];
    public string ActivePlacementPresetId { get; set; } = string.Empty;

    public static ScreenOutputMode DefaultOutputMode => OperatingSystem.IsWindows()
        ? ScreenOutputMode.SceneComposite
        : ScreenOutputMode.NativeOverlay;

    public bool Normalize()
    {
        return ConfigurationMigration.Normalize(this);
    }

    public BrowserScreenProfile GetActiveBrowserScreen()
    {
        Normalize();
        return BrowserScreens.FirstOrDefault(screen => screen.ScreenId == ActiveBrowserScreenId) ?? BrowserScreens[0];
    }

    public BrowserScreenProfile CreateDefaultBrowserScreen(string name, bool createdByIpc = false)
    {
        var screen = new BrowserScreenProfile
        {
            Name = name,
            CreatedByIpc = createdByIpc,
            Placement = new ScreenPlacementSettings(),
            SpatialAudioEnabled = createdByIpc,
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
        };
    }

    public BrowserScreenProfile Clone()
    {
        var clone = CloneAsNew(Name);
        clone.ScreenId = ScreenId;
        clone.CreatedByIpc = CreatedByIpc;
        clone.IpcOwnerId = IpcOwnerId;
        clone.SourceControlsLocked = SourceControlsLocked;
        clone.SourceControlsOwnerId = SourceControlsOwnerId;
        clone.LocalSequence = LocalSequence;
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
