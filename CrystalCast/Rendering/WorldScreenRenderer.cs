using System.Diagnostics;
using System.Numerics;
using CrystalCast.Video;
using Dalamud.Interface.Textures.TextureWraps;
using Pictomancy;

namespace CrystalCast.Rendering;

public sealed class WorldScreenRenderer : IDisposable
{
    private readonly Configuration configuration;
    private readonly DynamicVideoTexture dynamicTexture;
    private PctContext? pictomancyContext;
    private IVideoFrameSource? frameSource;
    private FfmpegAudioPlayer? audioPlayer;
    private string frameSourceSignature = string.Empty;
    private string audioSignature = string.Empty;
    private long lastFrameUnixMs;

    public WorldScreenRenderer(Configuration configuration)
    {
        this.configuration = configuration;
        dynamicTexture = new DynamicVideoTexture(Plugin.TextureProvider);

        try
        {
            pictomancyContext = PctService.Initialize(Plugin.PluginInterface, new PctOptions
            {
                EnableVfxRenderer = false,
                EnableKtkOutput = true,
                MaxImages = 16,
            });
            Status = "Pictomancy ready";
        }
        catch (Exception ex)
        {
            Status = $"Pictomancy init failed: {ex.Message}";
            Plugin.Log.Error(ex, "Failed to initialize Pictomancy.");
        }
    }

    public string Status { get; private set; } = "not initialized";
    public string SourceStatus => frameSource?.Status ?? "no dynamic source";
    public string AudioStatus => audioPlayer?.Status ?? "audio stopped";
    public string SourceName => frameSource?.Name ?? "no source";
    public double LastUploadMilliseconds => dynamicTexture.LastUploadMilliseconds;
    public long UploadCount => dynamicTexture.UploadCount;
    public int TextureWidth => dynamicTexture.Width;
    public int TextureHeight => dynamicTexture.Height;
    public long FrameAgeMilliseconds => lastFrameUnixMs == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs;
    public string LastDrawStatus { get; private set; } = "not drawn yet";
    public MediaPlaybackTelemetry? PlaybackTelemetry { get; private set; }

    public void DrawWorld()
    {
        if (!configuration.Enabled || pictomancyContext == null)
        {
            LastDrawStatus = configuration.Enabled ? "Pictomancy is not initialized" : "disabled";
            return;
        }

        var texture = ResolveTexture();
        if (texture == null)
        {
            LastDrawStatus = "no texture available yet";
            return;
        }

        var p = BuildDxParams();
        using var drawList = PctService.Draw(hints: new PctDrawHints
        {
            AutoDraw = GetAutoDraw(),
            AlphaBlendMode = AlphaBlendMode.Add,
            UIMask = GetUiMask(),
            DefaultParams = p,
        });

        if (drawList == null)
        {
            LastDrawStatus = "Pictomancy skipped this frame";
            return;
        }

        var center = GetCenter();
        var rotation = GetRotation();
        var panelSize = GetPanelSize(texture);
        var right = Vector3.Transform(Vector3.UnitX * panelSize.X, rotation);
        var down = Vector3.Transform(-Vector3.UnitY * panelSize.Y, rotation);
        drawList.AddImage(texture, center, right, down, p);

        if (configuration.ShowDebugMarker)
        {
            drawList.AddDot(center, 8.0f, 0xFF00FFFF);
            drawList.AddText(center + new Vector3(0, panelSize.Y * 0.65f, 0), 0xFF00FFFF, "CrystalCast", 1.0f);
        }

        LastDrawStatus = TryProjectCenter(out var screen)
            ? $"drawn; center on screen at {screen.X:0}, {screen.Y:0}"
            : "drawn; center is off-screen or behind camera";
    }

    public bool PlaceInFrontOfPlayer(float distanceMeters = 3.0f)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        var yaw = player.Rotation;
        var forward = new Vector3(MathF.Sin(yaw), 0.0f, MathF.Cos(yaw));
        var center = player.Position + (forward * distanceMeters) + (Vector3.UnitY * 1.4f);

        configuration.PositionX = center.X;
        configuration.PositionY = center.Y;
        configuration.PositionZ = center.Z;
        configuration.YawRadians = yaw + MathF.PI;
        configuration.PitchRadians = 0.0f;
        configuration.RollRadians = 0.0f;
        return true;
    }

    public bool TryProjectCenter(out Vector2 screenPosition)
    {
        return Plugin.GameGui.WorldToScreen(GetCenter(), out screenPosition);
    }

    public bool TryPlayDynamicSource()
    {
        configuration.PlaybackPaused = false;
        if (frameSource is not IMediaPlaybackController controller)
            return false;

        controller.Play();
        return true;
    }

    public bool TryPauseDynamicSource()
    {
        configuration.PlaybackPaused = true;
        if (frameSource is not IMediaPlaybackController controller)
            return false;

        controller.Pause();
        return true;
    }

    public bool TrySeekDynamicSourceBy(double seconds)
    {
        if (frameSource is not IMediaPlaybackController controller)
            return false;

        controller.SeekBy(seconds);
        return true;
    }

    public bool TryRestartDynamicSource()
    {
        configuration.PlaybackPaused = false;
        if (frameSource is not IMediaPlaybackController controller)
            return false;

        controller.Restart();
        return true;
    }

    public void Dispose()
    {
        frameSource?.Dispose();
        frameSource = null;
        audioPlayer?.Dispose();
        audioPlayer = null;
        dynamicTexture.Dispose();
        pictomancyContext?.Dispose();
        pictomancyContext = null;
    }

    private IDalamudTextureWrap? ResolveTexture()
    {
        EnsureFrameSource();
        if (frameSource == null)
        {
            StopAudio();
            PlaybackTelemetry = null;
            return null;
        }

        ApplyLiveSourceSettings();
        if (configuration.PlaybackPaused)
        {
            frameSource.Stop();
            StopAudio();
            UpdatePlaybackTelemetry();
            return dynamicTexture.TextureWrap;
        }

        frameSource.Start();
        UpdateAudio();
        UpdatePlaybackTelemetry();
        if (frameSource.TryGetLatestFrame(out var frame))
        {
            if (dynamicTexture.Upload(frame))
                lastFrameUnixMs = frame.TimestampUnixMs;
        }

        return dynamicTexture.TextureWrap;
    }

    private void EnsureFrameSource()
    {
        var signature = BuildFrameSourceSignature();
        if (signature == frameSourceSignature)
            return;

        frameSource?.Dispose();
        frameSource = null;
        frameSourceSignature = signature;

        switch (configuration.SourceKind)
        {
            case ScreenSourceKind.LocalVideo:
                frameSource = new FfmpegRawVideoFrameSource(
                    configuration.FfmpegPath,
                    configuration.LocalVideoPath,
                    configuration.LocalVideoScalePercent,
                    configuration.LocalVideoFps,
                    configuration.LoopLocalVideo,
                    configuration.LocalVideoWidth,
                    configuration.LocalVideoHeight);
                break;
            case ScreenSourceKind.YouTubeBrowser:
                frameSource = new YouTubeBrowserFrameSource(
                    configuration.YouTubeUrl,
                    configuration.YouTubeBrowserWidth,
                    configuration.YouTubeBrowserHeight,
                    configuration.YouTubeCaptureFps,
                    configuration.YouTubeAutoplay,
                    configuration.LoopYouTube,
                    configuration.YouTubeAudioEnabled,
                    configuration.YouTubeVolume,
                    configuration.YouTubePlaybackRate);
                break;
            default:
                StopAudio();
                Status = $"{configuration.SourceKind} source is not implemented yet";
                break;
        }
    }

    private string BuildFrameSourceSignature()
    {
        return configuration.SourceKind switch
        {
            ScreenSourceKind.LocalVideo => string.Join('|',
                configuration.SourceKind,
                configuration.FfmpegPath,
                configuration.LocalVideoPath,
                configuration.LocalVideoScalePercent,
                configuration.LocalVideoFps,
                configuration.LoopLocalVideo),
            ScreenSourceKind.YouTubeBrowser => string.Join('|',
                configuration.SourceKind,
                configuration.YouTubeUrl,
                configuration.YouTubeBrowserWidth,
                configuration.YouTubeBrowserHeight,
                configuration.YouTubeCaptureFps),
            _ => configuration.SourceKind.ToString(),
        };
    }

    private void UpdateAudio()
    {
        if (configuration.SourceKind != ScreenSourceKind.LocalVideo || !configuration.AudioEnabled)
        {
            StopAudio();
            return;
        }

        var signature = string.Join('|',
            configuration.FfmpegPath,
            configuration.LocalVideoPath,
            configuration.LoopLocalVideo);

        if (audioPlayer == null || signature != audioSignature)
        {
            audioPlayer?.Dispose();
            audioSignature = signature;
            audioPlayer = new FfmpegAudioPlayer(
                configuration.FfmpegPath,
                configuration.LocalVideoPath,
                configuration.LoopLocalVideo,
                configuration.AudioVolume);
        }

        audioPlayer.SetVolume(configuration.AudioVolume);
        audioPlayer.Start();
    }

    private void StopAudio()
    {
        audioPlayer?.Stop();
        audioSignature = string.Empty;
    }

    private void ApplyLiveSourceSettings()
    {
        if (configuration.SourceKind == ScreenSourceKind.YouTubeBrowser && frameSource is IMediaPlaybackController controller)
        {
            controller.ApplyPlaybackSettings(
                configuration.YouTubeAudioEnabled,
                configuration.YouTubeVolume,
                configuration.YouTubePlaybackRate,
                configuration.LoopYouTube);
        }
    }

    private void UpdatePlaybackTelemetry()
    {
        if (frameSource is IMediaPlaybackTelemetrySource telemetrySource && telemetrySource.TryGetPlaybackTelemetry(out var telemetry))
        {
            PlaybackTelemetry = telemetry;
            return;
        }

        PlaybackTelemetry = null;
    }

    private PctDxParams BuildDxParams()
    {
        return new PctDxParams
        {
            OccludedAlpha = Math.Clamp(configuration.OccludedAlpha, 0.0f, 1.0f),
            OcclusionTolerance = Math.Max(0.0f, configuration.OcclusionTolerance),
            FadeStart = configuration.EnableDistanceFade ? Math.Max(0.0f, configuration.FadeStartMeters) : float.PositiveInfinity,
            FadeStop = configuration.EnableDistanceFade ? Math.Max(configuration.FadeStartMeters + 0.01f, configuration.FadeStopMeters) : float.PositiveInfinity,
            ProjectionHeight = 0.0f,
        };
    }

    private static UIMask GetUiMask() => UIMask.None;

    private static AutoDraw GetAutoDraw() => AutoDraw.SceneComposite;

    private Vector2 GetPanelSize(IDalamudTextureWrap texture)
    {
        var width = Math.Max(0.01f, configuration.WidthMeters);
        var height = Math.Max(0.01f, configuration.HeightMeters);
        if (configuration.SourceKind is ScreenSourceKind.LocalVideo or ScreenSourceKind.YouTubeBrowser && texture.Width > 0 && texture.Height > 0)
            height = width * texture.Height / texture.Width;

        return new Vector2(width, height);
    }

    private Vector3 GetCenter()
    {
        return new Vector3(configuration.PositionX, configuration.PositionY, configuration.PositionZ);
    }

    private Quaternion GetRotation()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(
            configuration.YawRadians,
            configuration.PitchRadians,
            configuration.RollRadians);
        return Quaternion.Normalize(rotation);
    }
}
