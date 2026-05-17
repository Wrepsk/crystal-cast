using System.Diagnostics;
using System.Numerics;
using CrystalCast.Video;
using Dalamud.Interface.Textures.TextureWraps;
using Pictomancy;

namespace CrystalCast.Rendering;

public sealed class WorldScreenRenderer : IDisposable
{
    private readonly Configuration configuration;
    private readonly string bundledStaticImagePath;
    private readonly DynamicVideoTexture dynamicTexture;
    private PctContext? pictomancyContext;
    private IVideoFrameSource? frameSource;
    private FfmpegAudioPlayer? audioPlayer;
    private string frameSourceSignature = string.Empty;
    private string audioSignature = string.Empty;
    private long lastFrameUnixMs;

    public WorldScreenRenderer(Configuration configuration, string bundledStaticImagePath)
    {
        this.configuration = configuration;
        this.bundledStaticImagePath = bundledStaticImagePath;
        dynamicTexture = new DynamicVideoTexture(Plugin.TextureProvider);

        try
        {
            pictomancyContext = PctService.Initialize(Plugin.PluginInterface, new PctOptions
            {
                EnableVfxRenderer = false,
                EnableKtkOutput = false,
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
    public string SourceName => frameSource?.Name ?? "static image";
    public double LastUploadMilliseconds => dynamicTexture.LastUploadMilliseconds;
    public long UploadCount => dynamicTexture.UploadCount;
    public int TextureWidth => dynamicTexture.Width;
    public int TextureHeight => dynamicTexture.Height;
    public long FrameAgeMilliseconds => lastFrameUnixMs == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs;
    public string LastDrawStatus { get; private set; } = "not drawn yet";

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
            AutoDraw = AutoDraw.ImGuiOverlay,
            AlphaBlendMode = AlphaBlendMode.Add,
            UIMask = UIMask.None,
            DefaultParams = p,
        });

        if (drawList == null)
        {
            LastDrawStatus = "Pictomancy skipped this frame";
            return;
        }

        var center = GetCenter();
        var rotation = GetRotation();
        var right = Vector3.Transform(Vector3.UnitX * Math.Max(0.01f, configuration.WidthMeters), rotation);
        var down = Vector3.Transform(-Vector3.UnitY * Math.Max(0.01f, configuration.HeightMeters), rotation);
        drawList.AddImage(texture, center, right, down, p);

        if (configuration.ShowDebugMarker)
        {
            drawList.AddDot(center, 8.0f, 0xFF00FFFF);
            drawList.AddText(center + new Vector3(0, configuration.HeightMeters * 0.65f, 0), 0xFF00FFFF, "CrystalCast", 1.0f);
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
        if (configuration.SourceKind == ScreenSourceKind.StaticImage)
        {
            frameSource?.Dispose();
            frameSource = null;
            frameSourceSignature = string.Empty;
            StopAudio();

            var path = File.Exists(bundledStaticImagePath) ? bundledStaticImagePath : string.Empty;
            return string.IsNullOrEmpty(path)
                ? null
                : Plugin.TextureProvider.GetFromFileAbsolute(path).GetWrapOrDefault();
        }

        EnsureFrameSource();
        if (frameSource == null)
        {
            StopAudio();
            return null;
        }

        if (configuration.PlaybackPaused)
        {
            frameSource.Stop();
            StopAudio();
            return dynamicTexture.TextureWrap;
        }

        frameSource.Start();
        UpdateAudio();
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
            case ScreenSourceKind.Generated:
                frameSource = new GeneratedFrameSource(
                    configuration.GeneratedWidth,
                    configuration.GeneratedHeight,
                    configuration.GeneratedFps);
                break;
            case ScreenSourceKind.LocalVideo:
                frameSource = new FfmpegRawVideoFrameSource(
                    configuration.FfmpegPath,
                    configuration.LocalVideoPath,
                    configuration.LocalVideoWidth,
                    configuration.LocalVideoHeight,
                    configuration.LocalVideoFps,
                    configuration.LoopLocalVideo);
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
            ScreenSourceKind.Generated => string.Join('|',
                configuration.SourceKind,
                configuration.GeneratedWidth,
                configuration.GeneratedHeight,
                configuration.GeneratedFps),
            ScreenSourceKind.LocalVideo => string.Join('|',
                configuration.SourceKind,
                configuration.FfmpegPath,
                configuration.LocalVideoPath,
                configuration.LocalVideoWidth,
                configuration.LocalVideoHeight,
                configuration.LocalVideoFps,
                configuration.LoopLocalVideo),
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
