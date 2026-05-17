using Dalamud.Configuration;
using System;

namespace CrystalCast;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public ScreenSourceKind SourceKind { get; set; } = ScreenSourceKind.StaticImage;

    public string ScreenId { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerSessionId { get; set; } = Guid.NewGuid().ToString("N");
    public long LocalSequence { get; set; }

    public float PositionX { get; set; }
    public float PositionY { get; set; } = 1.6f;
    public float PositionZ { get; set; } = 3.0f;
    public float YawRadians { get; set; }
    public float PitchRadians { get; set; }
    public float RollRadians { get; set; }
    public float WidthMeters { get; set; } = 3.0f;
    public float HeightMeters { get; set; } = 1.6875f;

    public float OccludedAlpha { get; set; } = 0.0f;
    public float OcclusionTolerance { get; set; } = 0.02f;
    public bool EnableDistanceFade { get; set; }
    public float FadeStartMeters { get; set; } = 35.0f;
    public float FadeStopMeters { get; set; } = 60.0f;
    public int OutputMode { get; set; }
    public int UiMaskMode { get; set; }
    public bool ShowDebugMarker { get; set; } = true;

    public int GeneratedWidth { get; set; } = 512;
    public int GeneratedHeight { get; set; } = 288;
    public float GeneratedFps { get; set; } = 30.0f;

    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public string LocalVideoPath { get; set; } = string.Empty;
    public int LocalVideoWidth { get; set; } = 512;
    public int LocalVideoHeight { get; set; } = 288;
    public float LocalVideoScalePercent { get; set; } = 50.0f;
    public float LocalVideoFps { get; set; } = 30.0f;
    public bool LoopLocalVideo { get; set; } = true;
    public bool AudioEnabled { get; set; } = true;
    public float AudioVolume { get; set; } = 0.7f;
    public bool PlaybackPaused { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
