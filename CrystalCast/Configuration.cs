using Dalamud.Configuration;
using CrystalCast.Video;
using System;

namespace CrystalCast;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int OutputModeImGuiOverlay = 0;
    public const int OutputModeNativeOverlay = 1;
    public const int OutputModeSceneComposite = 2;
    public const int MaxBrowserScreens = 8;
    public const int MaxIpcBrowserScreens = 56;
    public const int MaxRenderableBrowserScreens = MaxBrowserScreens + MaxIpcBrowserScreens;

    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public ScreenSourceKind SourceKind { get; set; } = ScreenSourceKind.LocalVideo;

    public string ScreenId { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerSessionId { get; set; } = Guid.NewGuid().ToString("N");
    public long LocalSequence { get; set; }

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
    public int OutputMode { get; set; } = DefaultOutputMode;
    public int UiMaskMode { get; set; }
    public bool ShowDebugMarker { get; set; } = true;
    public bool PlacementGizmoEnabled { get; set; }
    public ScreenPlacementGizmoOperation PlacementGizmoOperation { get; set; } = ScreenPlacementGizmoOperation.Translate;

    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public string LocalVideoPath { get; set; } = string.Empty;
    public int LocalVideoWidth { get; set; } = 512;
    public int LocalVideoHeight { get; set; } = 288;
    public float LocalVideoScalePercent { get; set; } = 50.0f;
    public float LocalVideoFps { get; set; } = 30.0f;
    public bool LoopLocalVideo { get; set; } = true;
    public bool AudioEnabled { get; set; } = true;
    public float AudioVolume { get; set; } = 0.7f;
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

    public static int DefaultOutputMode => OperatingSystem.IsWindows()
        ? OutputModeSceneComposite
        : OutputModeNativeOverlay;

    public bool Normalize()
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(ScreenId))
        {
            ScreenId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(OwnerSessionId))
        {
            OwnerSessionId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (SourceKind is not (ScreenSourceKind.LocalVideo or ScreenSourceKind.YouTubeBrowser))
        {
            SourceKind = ScreenSourceKind.LocalVideo;
            changed = true;
        }

        if (LocalVideoPlacementMode is not (ScreenPlacementMode.World or ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera))
        {
            LocalVideoPlacementMode = ScreenPlacementMode.World;
            changed = true;
        }

        if (PlacementGizmoOperation is not (ScreenPlacementGizmoOperation.Translate or ScreenPlacementGizmoOperation.Rotate))
        {
            PlacementGizmoOperation = ScreenPlacementGizmoOperation.Translate;
            changed = true;
        }

        if (BrowserScreens == null)
        {
            BrowserScreens = [];
            changed = true;
        }
        if (BrowserScreens.Count == 0)
        {
            BrowserScreens.Add(CreateBrowserScreenFromLegacy("Browser screen 1"));
            changed = true;
        }

        var usedScreenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < BrowserScreens.Count; i++)
            changed |= BrowserScreens[i].Normalize($"Browser screen {i + 1}", usedScreenIds);

        if (string.IsNullOrWhiteSpace(ActiveBrowserScreenId) || BrowserScreens.All(screen => screen.ScreenId != ActiveBrowserScreenId))
        {
            ActiveBrowserScreenId = BrowserScreens[0].ScreenId;
            changed = true;
        }

        if (PlacementPresets == null)
        {
            PlacementPresets = [];
            changed = true;
        }

        var usedPresetIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < PlacementPresets.Count; i++)
            changed |= PlacementPresets[i].Normalize($"Placement {i + 1}", usedPresetIds);

        if (PlacementPresets.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(ActivePlacementPresetId))
            {
                ActivePlacementPresetId = string.Empty;
                changed = true;
            }
        }
        else if (string.IsNullOrWhiteSpace(ActivePlacementPresetId) || PlacementPresets.All(preset => preset.PresetId != ActivePlacementPresetId))
        {
            ActivePlacementPresetId = PlacementPresets[0].PresetId;
            changed = true;
        }

        return changed;
    }

    public BrowserScreenProfile GetActiveBrowserScreen()
    {
        Normalize();
        return BrowserScreens.FirstOrDefault(screen => screen.ScreenId == ActiveBrowserScreenId) ?? BrowserScreens[0];
    }

    public BrowserScreenProfile CreateDefaultBrowserScreen(string name)
    {
        var screen = new BrowserScreenProfile
        {
            Name = name,
            Placement = new ScreenPlacementSettings(),
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

    public ScreenPlacementSettings GetLocalVideoPlacement()
    {
        var placement = new ScreenPlacementSettings
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
        };
        placement.Normalize();
        return placement;
    }

    public void ApplyLocalVideoPlacement(ScreenPlacementSettings placement)
    {
        var copy = placement.Clone();
        copy.Normalize();
        LocalVideoPlacementMode = copy.Mode;
        PositionX = copy.PositionX;
        PositionY = copy.PositionY;
        PositionZ = copy.PositionZ;
        YawRadians = copy.YawRadians;
        PitchRadians = copy.PitchRadians;
        RollRadians = copy.RollRadians;
        WidthMeters = copy.WidthMeters;
        HeightMeters = copy.HeightMeters;
        ScreenCurveAmountMeters = copy.ScreenCurveAmountMeters;
        OccludedAlpha = copy.OccludedAlpha;
        OcclusionTolerance = copy.OcclusionTolerance;
        EnableDistanceFade = copy.EnableDistanceFade;
        FadeStartMeters = copy.FadeStartMeters;
        FadeStopMeters = copy.FadeStopMeters;
    }

    private BrowserScreenProfile CreateBrowserScreenFromLegacy(string name)
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
            YouTubeCaptureFpsManual = Math.Abs(YouTubeCaptureFps - 15.0f) > 0.01f,
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

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

public enum BrowserSourceProviderKind
{
    YouTube = 1,
    Twitch = 2,
    Dailymotion = 3,
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

    public bool SpatialAudioEnabled { get; set; } = true;
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

        if (YouTubeBrowserWidth <= 0)
        {
            YouTubeBrowserWidth = 1280;
            changed = true;
        }

        if (YouTubeBrowserHeight <= 0)
        {
            YouTubeBrowserHeight = 720;
            changed = true;
        }

        var captureFps = Math.Clamp(YouTubeCaptureFps, 1.0f, 60.0f);
        if (Math.Abs(YouTubeCaptureFps - captureFps) > 0.0001f)
        {
            YouTubeCaptureFps = captureFps;
            changed = true;
        }

        var volume = Math.Clamp(YouTubeVolume, 0.0f, 1.0f);
        if (Math.Abs(YouTubeVolume - volume) > 0.0001f)
        {
            YouTubeVolume = volume;
            changed = true;
        }

        var playbackRate = Math.Clamp(YouTubePlaybackRate, 0.25f, 2.0f);
        if (Math.Abs(YouTubePlaybackRate - playbackRate) > 0.0001f)
        {
            YouTubePlaybackRate = playbackRate;
            changed = true;
        }

        if (TwitchBrowserWidth <= 0)
        {
            TwitchBrowserWidth = 1920;
            changed = true;
        }

        if (TwitchBrowserHeight <= 0)
        {
            TwitchBrowserHeight = 1080;
            changed = true;
        }

        var twitchCaptureFps = Math.Clamp(TwitchCaptureFps, 1.0f, 60.0f);
        if (Math.Abs(TwitchCaptureFps - twitchCaptureFps) > 0.0001f)
        {
            TwitchCaptureFps = twitchCaptureFps;
            changed = true;
        }

        var twitchVolume = Math.Clamp(TwitchVolume, 0.0f, 1.0f);
        if (Math.Abs(TwitchVolume - twitchVolume) > 0.0001f)
        {
            TwitchVolume = twitchVolume;
            changed = true;
        }

        if (DailymotionBrowserWidth <= 0)
        {
            DailymotionBrowserWidth = 1280;
            changed = true;
        }

        if (DailymotionBrowserHeight <= 0)
        {
            DailymotionBrowserHeight = 720;
            changed = true;
        }

        var dailymotionCaptureFps = Math.Clamp(DailymotionCaptureFps, 1.0f, 60.0f);
        if (Math.Abs(DailymotionCaptureFps - dailymotionCaptureFps) > 0.0001f)
        {
            DailymotionCaptureFps = dailymotionCaptureFps;
            changed = true;
        }

        var dailymotionVolume = Math.Clamp(DailymotionVolume, 0.0f, 1.0f);
        if (Math.Abs(DailymotionVolume - dailymotionVolume) > 0.0001f)
        {
            DailymotionVolume = dailymotionVolume;
            changed = true;
        }

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
            SpatialAudioEnabled = SpatialAudioEnabled,
            SpatialAudioFullVolumeRadiusMeters = SpatialAudioFullVolumeRadiusMeters,
            SpatialAudioSilentRadiusMeters = SpatialAudioSilentRadiusMeters,
        };
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
