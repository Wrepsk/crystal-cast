using System.Diagnostics;
using System.Numerics;
using CrystalCast.Video;
using Dalamud.Interface.Textures.TextureWraps;
using Pictomancy;

namespace CrystalCast.Rendering;

public sealed class WorldScreenRenderer : IDisposable
{
    private const float AudioVolumeSmoothingSeconds = 0.45f;
    private const float ScreenCurveEpsilonMeters = 0.001f;
    private const int CurvedScreenSegments = 32;
    private static readonly TimeSpan SceneCompositeFallbackDelay = TimeSpan.FromSeconds(2);

    private readonly Configuration configuration;
    private readonly DynamicVideoTexture dynamicTexture;
    private PctContext? pictomancyContext;
    private IVideoFrameSource? frameSource;
    private FfmpegAudioPlayer? audioPlayer;
    private string frameSourceSignature = string.Empty;
    private string audioSignature = string.Empty;
    private long lastFrameUnixMs;
    private long lastEffectiveAudioVolumeUnixMs;
    private float smoothedEffectiveAudioVolume;
    private bool sceneCompositeFallbackActive;
    private bool sceneCompositeFallbackLogged;
    private string sceneCompositeFallbackReason = string.Empty;

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
                MaxImages = CurvedScreenSegments + 1,
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
    public float AudioDistanceMeters { get; private set; }
    public float SpatialAudioAttenuation { get; private set; } = 1.0f;
    public float EffectiveAudioVolume { get; private set; }

    public void DrawWorld()
    {
        UpdateSpatialAudioMetrics();

        if (!configuration.Enabled || pictomancyContext == null)
        {
            LastDrawStatus = configuration.Enabled ? "Pictomancy is not initialized" : "disabled";
            StopActiveSources();
            return;
        }

        var texture = ResolveTexture();
        if (texture == null)
        {
            LastDrawStatus = "no texture available yet";
            return;
        }

        var p = BuildDxParams();
        var autoDraw = ResolveAutoDraw(GetAutoDraw());
        using var drawList = PctService.Draw(hints: new PctDrawHints
        {
            AutoDraw = autoDraw,
            AlphaBlendMode = AlphaBlendMode.Add,
            UIMask = GetUiMask(),
            DefaultParams = p,
        });

        if (drawList == null)
        {
            LastDrawStatus = autoDraw == AutoDraw.SceneComposite
                ? $"scene composite waiting; {PctService.SceneCompositeStatus}"
                : "Pictomancy skipped this frame";
            return;
        }

        var center = GetCenter();
        var rotation = GetRotation();
        var panelSize = GetPanelSize(texture);
        var rightAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
        var down = Vector3.Transform(-Vector3.UnitY * panelSize.Y, rotation);
        DrawScreenImage(drawList, texture, center, rightAxis, down, panelSize, p);

        if (configuration.ShowDebugMarker)
        {
            drawList.AddDot(center, 8.0f, 0xFF00FFFF);
            drawList.AddText(center + new Vector3(0, panelSize.Y * 0.65f, 0), 0xFF00FFFF, "CrystalCast", 1.0f);
        }

        var curveAmount = GetScreenCurveAmount(panelSize.X);
        var shapeStatus = curveAmount > ScreenCurveEpsilonMeters ? $"curved {curveAmount:0.##} m" : "flat";
        var outputStatus = DescribeDrawMode(autoDraw);
        LastDrawStatus = TryProjectCenter(out var screen)
            ? $"{outputStatus} drawn {shapeStatus}; center on screen at {screen.X:0}, {screen.Y:0}"
            : $"{outputStatus} drawn {shapeStatus}; center is off-screen or behind camera";
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
            CalculateEffectiveAudioVolume(0.0f, smooth: false);
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
                    configuration.YouTubeBrowserEngine,
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
                configuration.YouTubeCaptureFps,
                configuration.YouTubeBrowserEngine),
            _ => configuration.SourceKind.ToString(),
        };
    }

    private void UpdateAudio()
    {
        if (configuration.SourceKind != ScreenSourceKind.LocalVideo)
        {
            StopAudio();
            return;
        }

        if (!configuration.AudioEnabled)
        {
            CalculateEffectiveAudioVolume(0.0f, smooth: false);
            StopAudio();
            return;
        }

        var effectiveVolume = CalculateEffectiveAudioVolume(configuration.AudioVolume);
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
                effectiveVolume);
        }

        audioPlayer.SetVolume(effectiveVolume);
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
            var effectiveVolume = configuration.YouTubeAudioEnabled
                ? CalculateEffectiveAudioVolume(configuration.YouTubeVolume)
                : CalculateEffectiveAudioVolume(0.0f, smooth: false);
            controller.ApplyPlaybackSettings(
                configuration.YouTubeAudioEnabled,
                effectiveVolume,
                configuration.YouTubePlaybackRate,
                configuration.LoopYouTube);
        }
    }

    private void StopActiveSources()
    {
        frameSource?.Stop();
        StopAudio();
        ResetEffectiveAudioVolume();
    }

    private void UpdateSpatialAudioMetrics()
    {
        SpatialAudioAttenuation = CalculateSpatialAudioAttenuation();
    }

    private float CalculateEffectiveAudioVolume(float baseVolume, bool smooth = true)
    {
        var targetVolume = Math.Clamp(baseVolume, 0.0f, 1.0f) * SpatialAudioAttenuation;
        if (!smooth || !configuration.SpatialAudioEnabled)
        {
            SetEffectiveAudioVolume(targetVolume);
            return EffectiveAudioVolume;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (lastEffectiveAudioVolumeUnixMs == 0)
        {
            smoothedEffectiveAudioVolume = targetVolume;
        }
        else
        {
            var deltaSeconds = Math.Clamp((now - lastEffectiveAudioVolumeUnixMs) / 1000.0f, 0.0f, 0.25f);
            var alpha = 1.0f - MathF.Exp(-deltaSeconds / AudioVolumeSmoothingSeconds);
            smoothedEffectiveAudioVolume += (targetVolume - smoothedEffectiveAudioVolume) * alpha;
            if (targetVolume <= 0.0f && smoothedEffectiveAudioVolume < 0.005f)
                smoothedEffectiveAudioVolume = 0.0f;
            else if (MathF.Abs(targetVolume - smoothedEffectiveAudioVolume) < 0.0005f)
                smoothedEffectiveAudioVolume = targetVolume;
        }

        lastEffectiveAudioVolumeUnixMs = now;
        EffectiveAudioVolume = Math.Clamp(smoothedEffectiveAudioVolume, 0.0f, 1.0f);
        return EffectiveAudioVolume;
    }

    private void SetEffectiveAudioVolume(float volume)
    {
        smoothedEffectiveAudioVolume = Math.Clamp(volume, 0.0f, 1.0f);
        EffectiveAudioVolume = smoothedEffectiveAudioVolume;
        lastEffectiveAudioVolumeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void ResetEffectiveAudioVolume()
    {
        smoothedEffectiveAudioVolume = 0.0f;
        EffectiveAudioVolume = 0.0f;
        lastEffectiveAudioVolumeUnixMs = 0;
    }

    private float CalculateSpatialAudioAttenuation()
    {
        if (!configuration.SpatialAudioEnabled)
        {
            AudioDistanceMeters = 0.0f;
            return 1.0f;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            AudioDistanceMeters = 0.0f;
            return 1.0f;
        }

        AudioDistanceMeters = DistanceToScreen(player.Position);
        var fullRadius = Math.Max(0.0f, configuration.SpatialAudioFullVolumeRadiusMeters);
        var silentRadius = Math.Max(fullRadius + 0.1f, configuration.SpatialAudioSilentRadiusMeters);

        if (AudioDistanceMeters <= fullRadius)
            return 1.0f;

        if (AudioDistanceMeters >= silentRadius)
            return 0.0f;

        var t = (AudioDistanceMeters - fullRadius) / (silentRadius - fullRadius);
        var smooth = t * t * (3.0f - (2.0f * t));
        return Math.Clamp(1.0f - smooth, 0.0f, 1.0f);
    }

    private float DistanceToScreen(Vector3 position)
    {
        var rotation = GetRotation();
        var right = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
        var down = Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, rotation));
        var panelSize = GetPanelSizeForSource();
        var curveAmount = GetScreenCurveAmount(panelSize.X);
        if (curveAmount > ScreenCurveEpsilonMeters)
            return DistanceToCurvedScreen(position, GetCenter(), right, down * panelSize.Y, panelSize.X, curveAmount);

        var normal = Vector3.Normalize(Vector3.Cross(right, down));
        var halfWidth = panelSize.X * 0.5f;
        var halfHeight = panelSize.Y * 0.5f;
        var offset = position - GetCenter();
        var localX = Vector3.Dot(offset, right);
        var localY = Vector3.Dot(offset, down);
        var localZ = Vector3.Dot(offset, normal);
        var outsideX = MathF.Max(MathF.Abs(localX) - halfWidth, 0.0f);
        var outsideY = MathF.Max(MathF.Abs(localY) - halfHeight, 0.0f);

        return MathF.Sqrt((outsideX * outsideX) + (outsideY * outsideY) + (localZ * localZ));
    }

    private void DrawScreenImage(PctDrawList drawList, IDalamudTextureWrap texture, Vector3 center, Vector3 rightAxis, Vector3 down, Vector2 panelSize, PctDxParams p)
    {
        var curveAmount = GetScreenCurveAmount(panelSize.X);
        if (curveAmount <= ScreenCurveEpsilonMeters)
        {
            drawList.AddImage(texture, center, rightAxis * panelSize.X, down, p);
            return;
        }

        var downAxis = Vector3.Normalize(down);
        var curveForward = -Vector3.Normalize(Vector3.Cross(rightAxis, downAxis));
        var curve = BuildCurve(panelSize.X, curveAmount);
        for (var i = 0; i < CurvedScreenSegments; i++)
        {
            var u0 = (float)i / CurvedScreenSegments;
            var u1 = (float)(i + 1) / CurvedScreenSegments;
            var start = GetCurvedScreenPoint(center, rightAxis, curveForward, curve.Radius, curve.HalfAngle, u0);
            var stop = GetCurvedScreenPoint(center, rightAxis, curveForward, curve.Radius, curve.HalfAngle, u1);
            var stripRight = stop - start;
            var stripCenter = (start + stop) * 0.5f;

            drawList.AddImage(
                texture,
                stripCenter,
                stripRight,
                down,
                new Vector2(u0, 0.0f),
                new Vector2(u1, 1.0f),
                p);
        }
    }

    private float DistanceToCurvedScreen(Vector3 position, Vector3 center, Vector3 rightAxis, Vector3 down, float width, float curveAmount)
    {
        var downAxis = Vector3.Normalize(down);
        var curveForward = -Vector3.Normalize(Vector3.Cross(rightAxis, downAxis));
        var curve = BuildCurve(width, curveAmount);
        var minDistanceSquared = float.PositiveInfinity;

        for (var i = 0; i < CurvedScreenSegments; i++)
        {
            var u0 = (float)i / CurvedScreenSegments;
            var u1 = (float)(i + 1) / CurvedScreenSegments;
            var start = GetCurvedScreenPoint(center, rightAxis, curveForward, curve.Radius, curve.HalfAngle, u0);
            var stop = GetCurvedScreenPoint(center, rightAxis, curveForward, curve.Radius, curve.HalfAngle, u1);
            var stripRight = stop - start;
            var stripCenter = (start + stop) * 0.5f;

            minDistanceSquared = MathF.Min(minDistanceSquared, DistanceSquaredToPanel(position, stripCenter, stripRight, down));
        }

        return MathF.Sqrt(minDistanceSquared);
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

    private AutoDraw GetAutoDraw()
    {
        return configuration.OutputMode switch
        {
            Configuration.OutputModeNativeOverlay => AutoDraw.NativeOverlay,
            Configuration.OutputModeSceneComposite or 3 => AutoDraw.SceneComposite,
            _ => AutoDraw.ImGuiOverlay,
        };
    }

    private AutoDraw ResolveAutoDraw(AutoDraw configuredAutoDraw)
    {
        if (configuredAutoDraw != AutoDraw.SceneComposite)
        {
            sceneCompositeFallbackActive = false;
            sceneCompositeFallbackLogged = false;
            sceneCompositeFallbackReason = string.Empty;
            return configuredAutoDraw;
        }

        if (sceneCompositeFallbackActive)
            return AutoDraw.NativeOverlay;

        var fallbackReason = string.Empty;
        if (PctService.IsSceneCompositeHookUnavailable)
            fallbackReason = $"scene composite hook unavailable; {PctService.SceneCompositeStatus}";
        else if (PctService.IsSceneCompositeStalled(SceneCompositeFallbackDelay))
            fallbackReason = $"scene composite stalled for {SceneCompositeFallbackDelay.TotalSeconds:0.#}s; {PctService.SceneCompositeStatus}";

        if (string.IsNullOrWhiteSpace(fallbackReason))
            return AutoDraw.SceneComposite;

        sceneCompositeFallbackActive = true;
        sceneCompositeFallbackReason = fallbackReason;
        PctService.CancelSceneComposite($"CrystalCast switching to NativeOverlay fallback: {fallbackReason}");

        if (!sceneCompositeFallbackLogged)
        {
            sceneCompositeFallbackLogged = true;
            Plugin.Log.Warning($"Scene composite is unavailable; using NativeOverlay fallback. {fallbackReason}");
        }

        return AutoDraw.NativeOverlay;
    }

    private string DescribeDrawMode(AutoDraw autoDraw)
    {
        return autoDraw switch
        {
            AutoDraw.SceneComposite => "scene composite",
            AutoDraw.NativeOverlay when sceneCompositeFallbackActive => $"native overlay fallback ({Abbreviate(sceneCompositeFallbackReason, 96)})",
            AutoDraw.NativeOverlay => "native overlay",
            AutoDraw.ImGuiOverlay => "ImGui overlay",
            _ => "manual",
        };
    }

    private static string Abbreviate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value;

        return $"{value[..maxLength]}...";
    }

    private Vector2 GetPanelSize(IDalamudTextureWrap texture)
    {
        var width = Math.Max(0.01f, configuration.WidthMeters);
        var height = Math.Max(0.01f, configuration.HeightMeters);
        if (configuration.SourceKind is ScreenSourceKind.LocalVideo or ScreenSourceKind.YouTubeBrowser && texture.Width > 0 && texture.Height > 0)
            height = width * texture.Height / texture.Width;

        return new Vector2(width, height);
    }

    private Vector2 GetPanelSizeForSource()
    {
        var width = Math.Max(0.01f, configuration.WidthMeters);
        var height = Math.Max(0.01f, configuration.HeightMeters);
        if (frameSource is { Width: > 0, Height: > 0 })
            height = width * frameSource.Height / frameSource.Width;

        return new Vector2(width, height);
    }

    private float GetScreenCurveAmount(float width)
    {
        return Math.Clamp(configuration.ScreenCurveAmountMeters, 0.0f, GetMaxScreenCurveAmount(width));
    }

    private static (float Radius, float HalfAngle) BuildCurve(float width, float curveAmount)
    {
        var halfArcLength = width * 0.5f;
        var targetDepth = Math.Clamp(curveAmount, 0.0f, GetMaxScreenCurveAmount(width));
        var low = 0.0f;
        var high = MathF.PI * 0.5f;

        for (var i = 0; i < 32; i++)
        {
            var mid = (low + high) * 0.5f;
            var depth = halfArcLength * (1.0f - MathF.Cos(mid)) / mid;
            if (depth < targetDepth)
                low = mid;
            else
                high = mid;
        }

        var halfAngle = (low + high) * 0.5f;
        var radius = halfArcLength / halfAngle;
        return (radius, halfAngle);
    }

    private static float GetMaxScreenCurveAmount(float width)
    {
        return Math.Max(0.0f, width / MathF.PI);
    }

    private static Vector3 GetCurvedScreenPoint(Vector3 center, Vector3 rightAxis, Vector3 curveForward, float radius, float halfAngle, float u)
    {
        var theta = ((u * 2.0f) - 1.0f) * halfAngle;
        var horizontal = radius * MathF.Sin(theta);
        var depth = radius * (1.0f - MathF.Cos(theta));
        return center + (rightAxis * horizontal) + (curveForward * depth);
    }

    private static float DistanceSquaredToPanel(Vector3 position, Vector3 center, Vector3 right, Vector3 down)
    {
        var rightLength = right.Length();
        var downLength = down.Length();
        if (rightLength <= 0.0001f || downLength <= 0.0001f)
            return Vector3.DistanceSquared(position, center);

        var rightAxis = right / rightLength;
        var downAxis = down / downLength;
        var normal = Vector3.Normalize(Vector3.Cross(rightAxis, downAxis));
        var offset = position - center;
        var localX = Vector3.Dot(offset, rightAxis);
        var localY = Vector3.Dot(offset, downAxis);
        var localZ = Vector3.Dot(offset, normal);
        var outsideX = MathF.Max(MathF.Abs(localX) - (rightLength * 0.5f), 0.0f);
        var outsideY = MathF.Max(MathF.Abs(localY) - (downLength * 0.5f), 0.0f);

        return (outsideX * outsideX) + (outsideY * outsideY) + (localZ * localZ);
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
