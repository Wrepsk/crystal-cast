using System.Numerics;
using CrystalCast.Video;
using Dalamud.Interface.Textures.TextureWraps;
using Pictomancy;

namespace CrystalCast.Rendering;

public sealed class WorldScreenManager : IDisposable
{
    private const int CurvedScreenSegments = 32;

    private readonly Configuration configuration;
    private readonly WorldScreenInstance localScreen;
    private readonly Dictionary<string, WorldScreenInstance> browserScreens = new(StringComparer.Ordinal);
    private PctContext? pictomancyContext;

    public WorldScreenManager(Configuration configuration)
    {
        this.configuration = configuration;
        localScreen = new WorldScreenInstance(configuration);

        try
        {
            pictomancyContext = PctService.Initialize(Plugin.PluginInterface, new PctOptions
            {
                EnableVfxRenderer = false,
                EnableKtkOutput = true,
                MaxImages = (Configuration.MaxBrowserScreens * CurvedScreenSegments) + 1,
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
    public string LastDrawStatus { get; private set; } = "not drawn yet";
    public string SourceStatus => ActiveInstance?.SourceStatus ?? "no dynamic source";
    public string AudioStatus => ActiveInstance?.AudioStatus ?? "audio stopped";
    public string SourceName => ActiveInstance?.SourceName ?? "no source";
    public double LastUploadMilliseconds => ActiveInstance?.LastUploadMilliseconds ?? 0.0;
    public long UploadCount => ActiveInstance?.UploadCount ?? 0;
    public int TextureWidth => ActiveInstance?.TextureWidth ?? 0;
    public int TextureHeight => ActiveInstance?.TextureHeight ?? 0;
    public long FrameAgeMilliseconds => ActiveInstance?.FrameAgeMilliseconds ?? 0;
    public MediaPlaybackTelemetry? PlaybackTelemetry => ActiveInstance?.PlaybackTelemetry;
    public float AudioDistanceMeters => ActiveInstance?.AudioDistanceMeters ?? 0.0f;
    public float SpatialAudioAttenuation => ActiveInstance?.SpatialAudioAttenuation ?? 1.0f;
    public float EffectiveAudioVolume => ActiveInstance?.EffectiveAudioVolume ?? 0.0f;
    public int ActiveBrowserRuntimeCount => browserScreens.Count;

    private WorldScreenInstance? ActiveInstance
    {
        get
        {
            if (configuration.SourceKind == ScreenSourceKind.YouTubeBrowser)
            {
                SyncBrowserScreens();
                var activeScreen = configuration.GetActiveBrowserScreen();
                return browserScreens.TryGetValue(activeScreen.ScreenId, out var instance) ? instance : null;
            }

            return localScreen;
        }
    }

    public void DrawWorld()
    {
        configuration.Normalize();
        SyncBrowserScreens();

        if (pictomancyContext == null)
        {
            LastDrawStatus = "Pictomancy is not initialized";
            StopAllScreens();
            return;
        }

        if (!configuration.Enabled)
        {
            LastDrawStatus = "disabled";
            StopAllScreens();
            return;
        }

        if (configuration.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            localScreen.Stop();
            DrawBrowserScreens();
            return;
        }

        StopBrowserScreens();
        DrawScreens([localScreen]);
    }

    public bool PlaceInFrontOfPlayer(float distanceMeters = 3.0f)
    {
        return ActiveInstance?.PlaceInFrontOfPlayer(distanceMeters) == true;
    }

    public bool PlaceBrowserScreenInFrontOfPlayer(BrowserScreenProfile screen, float distanceMeters = 3.0f)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance)
            ? instance.PlaceInFrontOfPlayer(distanceMeters)
            : WorldScreenInstance.PlacePlacementInFrontOfPlayer(screen.Placement, distanceMeters);
    }

    public bool TryPlayDynamicSource()
    {
        return ActiveInstance?.TryPlayDynamicSource() == true;
    }

    public bool TryPauseDynamicSource()
    {
        return ActiveInstance?.TryPauseDynamicSource() == true;
    }

    public bool TrySeekDynamicSourceBy(double seconds)
    {
        return ActiveInstance?.TrySeekDynamicSourceBy(seconds) == true;
    }

    public bool TryRestartDynamicSource()
    {
        return ActiveInstance?.TryRestartDynamicSource() == true;
    }

    public MediaPlaybackTelemetry? GetPlaybackTelemetry(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance)
            ? instance.PlaybackTelemetry
            : null;
    }

    public string GetSourceStatus(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance)
            ? instance.SourceStatus
            : "browser source not started";
    }

    public void Dispose()
    {
        localScreen.Dispose();
        foreach (var instance in browserScreens.Values)
            instance.Dispose();
        browserScreens.Clear();
        pictomancyContext?.Dispose();
        pictomancyContext = null;
    }

    private void DrawBrowserScreens()
    {
        var screens = configuration.BrowserScreens
            .Take(Configuration.MaxBrowserScreens)
            .Where(screen => screen.Enabled)
            .Select(screen => browserScreens[screen.ScreenId]);

        DrawScreens(screens);
    }

    private void DrawScreens(IEnumerable<WorldScreenInstance> screens)
    {
        var prepared = new List<(WorldScreenInstance Instance, IDalamudTextureWrap Texture)>();
        foreach (var screen in screens)
        {
            if (screen.TryPrepareFrame(out var texture) && texture != null)
                prepared.Add((screen, texture));
        }

        if (prepared.Count == 0)
        {
            LastDrawStatus = "no texture available yet";
            return;
        }

        var autoDraw = GetAutoDraw();
        using var drawList = PctService.Draw(hints: new PctDrawHints
        {
            AutoDraw = autoDraw,
            AlphaBlendMode = AlphaBlendMode.Add,
            UIMask = UIMask.None,
        });

        if (drawList == null)
        {
            LastDrawStatus = autoDraw == AutoDraw.SceneComposite
                ? $"scene composite waiting; {PctService.SceneCompositeStatus}"
                : "Pictomancy skipped this frame";
            return;
        }

        foreach (var (screen, texture) in prepared)
            screen.DrawPrepared(drawList, texture, configuration.ShowDebugMarker);

        LastDrawStatus = $"{DescribeDrawMode(autoDraw)} drawn {prepared.Count} screen{(prepared.Count == 1 ? string.Empty : "s")}";
    }

    private void SyncBrowserScreens()
    {
        configuration.Normalize();
        var activeIds = configuration.BrowserScreens
            .Take(Configuration.MaxBrowserScreens)
            .Select(screen => screen.ScreenId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var screen in configuration.BrowserScreens.Take(Configuration.MaxBrowserScreens))
        {
            if (!browserScreens.ContainsKey(screen.ScreenId))
                browserScreens[screen.ScreenId] = new WorldScreenInstance(configuration, screen);
        }

        foreach (var (screenId, instance) in browserScreens.ToArray())
        {
            if (activeIds.Contains(screenId))
                continue;

            instance.Dispose();
            browserScreens.Remove(screenId);
        }

        foreach (var screen in configuration.BrowserScreens.Take(Configuration.MaxBrowserScreens).Where(screen => !screen.Enabled))
        {
            if (browserScreens.TryGetValue(screen.ScreenId, out var instance))
                instance.Stop();
        }
    }

    private void StopAllScreens()
    {
        localScreen.Stop();
        StopBrowserScreens();
    }

    private void StopBrowserScreens()
    {
        foreach (var instance in browserScreens.Values)
            instance.Stop();
    }

    private AutoDraw GetAutoDraw()
    {
        return configuration.OutputMode switch
        {
            Configuration.OutputModeNativeOverlay => AutoDraw.NativeOverlay,
            Configuration.OutputModeSceneComposite or 3 => AutoDraw.SceneComposite,
            _ => AutoDraw.ImGuiOverlay,
        };
    }

    private static string DescribeDrawMode(AutoDraw autoDraw)
    {
        return autoDraw switch
        {
            AutoDraw.SceneComposite => "scene composite",
            AutoDraw.NativeOverlay => "native overlay",
            AutoDraw.ImGuiOverlay => "ImGui overlay",
            _ => "manual",
        };
    }

    private sealed class WorldScreenInstance : IDisposable
    {
        private const float AudioVolumeSmoothingSeconds = 0.45f;
        private const float ScreenCurveEpsilonMeters = 0.001f;

        private readonly Configuration configuration;
        private readonly BrowserScreenProfile? browserScreen;
        private readonly DynamicVideoTexture dynamicTexture;
        private IVideoFrameSource? frameSource;
        private FfmpegAudioPlayer? audioPlayer;
        private string frameSourceSignature = string.Empty;
        private string audioSignature = string.Empty;
        private long lastFrameUnixMs;
        private long lastEffectiveAudioVolumeUnixMs;
        private float smoothedEffectiveAudioVolume;

        public WorldScreenInstance(Configuration configuration, BrowserScreenProfile? browserScreen = null)
        {
            this.configuration = configuration;
            this.browserScreen = browserScreen;
            dynamicTexture = new DynamicVideoTexture(Plugin.TextureProvider);
        }

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

        public bool TryPrepareFrame(out IDalamudTextureWrap? texture)
        {
            UpdateSpatialAudioMetrics();

            if (!IsEnabled())
            {
                LastDrawStatus = "disabled";
                Stop();
                texture = null;
                return false;
            }

            texture = ResolveTexture();
            if (texture == null)
            {
                LastDrawStatus = "no texture available yet";
                return false;
            }

            return true;
        }

        public void DrawPrepared(PctDrawList drawList, IDalamudTextureWrap texture, bool showDebugMarker)
        {
            var p = BuildDxParams();
            var center = GetCenter();
            var rotation = GetRotation();
            var panelSize = GetPanelSize(texture);
            var rightAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
            var down = Vector3.Transform(-Vector3.UnitY * panelSize.Y, rotation);
            DrawScreenImage(drawList, texture, center, rightAxis, down, panelSize, p);

            if (showDebugMarker)
            {
                drawList.AddDot(center, 8.0f, 0xFF00FFFF);
                drawList.AddText(center + new Vector3(0, panelSize.Y * 0.65f, 0), 0xFF00FFFF, browserScreen?.Name ?? "CrystalCast", 1.0f);
            }

            var curveAmount = GetScreenCurveAmount(panelSize.X);
            var shapeStatus = curveAmount > ScreenCurveEpsilonMeters ? $"curved {curveAmount:0.##} m" : "flat";
            LastDrawStatus = TryProjectCenter(out var screen)
                ? $"drawn {shapeStatus}; center on screen at {screen.X:0}, {screen.Y:0}"
                : $"drawn {shapeStatus}; center is off-screen or behind camera";
        }

        public bool PlaceInFrontOfPlayer(float distanceMeters = 3.0f)
        {
            if (browserScreen != null)
                return PlacePlacementInFrontOfPlayer(browserScreen.Placement, distanceMeters);

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

        public static bool PlacePlacementInFrontOfPlayer(ScreenPlacementSettings placement, float distanceMeters = 3.0f)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
                return false;

            var yaw = player.Rotation;
            var forward = new Vector3(MathF.Sin(yaw), 0.0f, MathF.Cos(yaw));
            var center = player.Position + (forward * distanceMeters) + (Vector3.UnitY * 1.4f);

            placement.PositionX = center.X;
            placement.PositionY = center.Y;
            placement.PositionZ = center.Z;
            placement.YawRadians = yaw + MathF.PI;
            placement.PitchRadians = 0.0f;
            placement.RollRadians = 0.0f;
            return true;
        }

        public bool TryProjectCenter(out Vector2 screenPosition)
        {
            return Plugin.GameGui.WorldToScreen(GetCenter(), out screenPosition);
        }

        public bool TryPlayDynamicSource()
        {
            SetPlaybackPaused(false);
            if (frameSource is not IMediaPlaybackController controller)
                return false;

            controller.Play();
            return true;
        }

        public bool TryPauseDynamicSource()
        {
            SetPlaybackPaused(true);
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
            SetPlaybackPaused(false);
            if (frameSource is not IMediaPlaybackController controller)
                return false;

            controller.Restart();
            return true;
        }

        public void Stop()
        {
            frameSource?.Stop();
            StopAudio();
            ResetEffectiveAudioVolume();
            PlaybackTelemetry = null;
        }

        public void Dispose()
        {
            frameSource?.Dispose();
            frameSource = null;
            audioPlayer?.Dispose();
            audioPlayer = null;
            dynamicTexture.Dispose();
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
            if (IsPlaybackPaused())
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

            switch (GetSourceKind())
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
                case ScreenSourceKind.YouTubeBrowser when browserScreen != null:
                    frameSource = new YouTubeBrowserFrameSource(
                        browserScreen.YouTubeUrl,
                        browserScreen.YouTubeBrowserWidth,
                        browserScreen.YouTubeBrowserHeight,
                        browserScreen.YouTubeCaptureFps,
                        configuration.YouTubeBrowserEngine,
                        browserScreen.YouTubeAutoplay,
                        browserScreen.LoopYouTube,
                        browserScreen.YouTubeAudioEnabled,
                        browserScreen.YouTubeVolume,
                        browserScreen.YouTubePlaybackRate);
                    break;
            }
        }

        private string BuildFrameSourceSignature()
        {
            return GetSourceKind() switch
            {
                ScreenSourceKind.LocalVideo => string.Join('|',
                    ScreenSourceKind.LocalVideo,
                    configuration.FfmpegPath,
                    configuration.LocalVideoPath,
                    configuration.LocalVideoScalePercent,
                    configuration.LocalVideoFps,
                    configuration.LoopLocalVideo),
                ScreenSourceKind.YouTubeBrowser when browserScreen != null => string.Join('|',
                    ScreenSourceKind.YouTubeBrowser,
                    browserScreen.ProviderKind,
                    browserScreen.YouTubeUrl,
                    browserScreen.YouTubeBrowserWidth,
                    browserScreen.YouTubeBrowserHeight,
                    browserScreen.YouTubeCaptureFps,
                    configuration.YouTubeBrowserEngine),
                _ => GetSourceKind().ToString(),
            };
        }

        private void UpdateAudio()
        {
            if (GetSourceKind() != ScreenSourceKind.LocalVideo)
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
            if (browserScreen == null || frameSource is not IMediaPlaybackController controller)
                return;

            var effectiveVolume = browserScreen.YouTubeAudioEnabled
                ? CalculateEffectiveAudioVolume(browserScreen.YouTubeVolume)
                : CalculateEffectiveAudioVolume(0.0f, smooth: false);
            controller.ApplyPlaybackSettings(
                browserScreen.YouTubeAudioEnabled,
                effectiveVolume,
                browserScreen.YouTubePlaybackRate,
                browserScreen.LoopYouTube);
        }

        private void UpdateSpatialAudioMetrics()
        {
            SpatialAudioAttenuation = CalculateSpatialAudioAttenuation();
        }

        private float CalculateEffectiveAudioVolume(float baseVolume, bool smooth = true)
        {
            var targetVolume = Math.Clamp(baseVolume, 0.0f, 1.0f) * SpatialAudioAttenuation;
            if (!smooth || !IsSpatialAudioEnabled())
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
            if (!IsSpatialAudioEnabled())
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
            var fullRadius = Math.Max(0.0f, GetSpatialFullVolumeRadiusMeters());
            var silentRadius = Math.Max(fullRadius + 0.1f, GetSpatialSilentRadiusMeters());

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
            var placement = browserScreen?.Placement;
            return new PctDxParams
            {
                OccludedAlpha = Math.Clamp(placement?.OccludedAlpha ?? configuration.OccludedAlpha, 0.0f, 1.0f),
                OcclusionTolerance = Math.Max(0.0f, placement?.OcclusionTolerance ?? configuration.OcclusionTolerance),
                FadeStart = GetDistanceFadeEnabled() ? Math.Max(0.0f, placement?.FadeStartMeters ?? configuration.FadeStartMeters) : float.PositiveInfinity,
                FadeStop = GetDistanceFadeEnabled()
                    ? Math.Max((placement?.FadeStartMeters ?? configuration.FadeStartMeters) + 0.01f, placement?.FadeStopMeters ?? configuration.FadeStopMeters)
                    : float.PositiveInfinity,
                ProjectionHeight = 0.0f,
            };
        }

        private Vector2 GetPanelSize(IDalamudTextureWrap texture)
        {
            var width = Math.Max(0.01f, GetWidthMeters());
            var height = Math.Max(0.01f, GetHeightMeters());
            if (texture.Width > 0 && texture.Height > 0)
                height = width * texture.Height / texture.Width;

            return new Vector2(width, height);
        }

        private Vector2 GetPanelSizeForSource()
        {
            var width = Math.Max(0.01f, GetWidthMeters());
            var height = Math.Max(0.01f, GetHeightMeters());
            if (frameSource is { Width: > 0, Height: > 0 })
                height = width * frameSource.Height / frameSource.Width;

            return new Vector2(width, height);
        }

        private float GetScreenCurveAmount(float width)
        {
            return Math.Clamp(GetScreenCurveAmountMeters(), 0.0f, GetMaxScreenCurveAmount(width));
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

        private ScreenSourceKind GetSourceKind() => browserScreen == null ? ScreenSourceKind.LocalVideo : ScreenSourceKind.YouTubeBrowser;
        private bool IsEnabled() => browserScreen?.Enabled ?? configuration.Enabled;
        private bool IsPlaybackPaused() => browserScreen?.PlaybackPaused ?? configuration.PlaybackPaused;

        private void SetPlaybackPaused(bool paused)
        {
            if (browserScreen != null)
                browserScreen.PlaybackPaused = paused;
            else
                configuration.PlaybackPaused = paused;
        }

        private bool IsSpatialAudioEnabled() => browserScreen?.SpatialAudioEnabled ?? configuration.SpatialAudioEnabled;
        private float GetSpatialFullVolumeRadiusMeters() => browserScreen?.SpatialAudioFullVolumeRadiusMeters ?? configuration.SpatialAudioFullVolumeRadiusMeters;
        private float GetSpatialSilentRadiusMeters() => browserScreen?.SpatialAudioSilentRadiusMeters ?? configuration.SpatialAudioSilentRadiusMeters;
        private bool GetDistanceFadeEnabled() => browserScreen?.Placement.EnableDistanceFade ?? configuration.EnableDistanceFade;
        private float GetWidthMeters() => browserScreen?.Placement.WidthMeters ?? configuration.WidthMeters;
        private float GetHeightMeters() => browserScreen?.Placement.HeightMeters ?? configuration.HeightMeters;
        private float GetScreenCurveAmountMeters() => browserScreen?.Placement.ScreenCurveAmountMeters ?? configuration.ScreenCurveAmountMeters;

        private Vector3 GetCenter()
        {
            return browserScreen == null
                ? new Vector3(configuration.PositionX, configuration.PositionY, configuration.PositionZ)
                : new Vector3(browserScreen.Placement.PositionX, browserScreen.Placement.PositionY, browserScreen.Placement.PositionZ);
        }

        private Quaternion GetRotation()
        {
            var rotation = browserScreen == null
                ? Quaternion.CreateFromYawPitchRoll(
                    configuration.YawRadians,
                    configuration.PitchRadians,
                    configuration.RollRadians)
                : Quaternion.CreateFromYawPitchRoll(
                    browserScreen.Placement.YawRadians,
                    browserScreen.Placement.PitchRadians,
                    browserScreen.Placement.RollRadians);
            return Quaternion.Normalize(rotation);
        }
    }
}
