using System.Numerics;
using CrystalCast.Video;
using Dalamud.Interface.Textures.TextureWraps;
using Pictomancy;

namespace CrystalCast.Rendering;

public sealed class WorldScreenManager : IDisposable
{
    private const int CurvedScreenSegments = 32;

    private readonly record struct PreparedScreenTexture(nint NativeHandle, int Width, int Height);

    private readonly Configuration configuration;
    private readonly Dictionary<string, WorldScreenInstance> browserScreens = new(StringComparer.Ordinal);
    private PctContext? pictomancyContext;

    public WorldScreenManager(Configuration configuration)
    {
        this.configuration = configuration;

        try
        {
            pictomancyContext = PctService.Initialize(Plugin.PluginInterface, new PctOptions
            {
                EnableVfxRenderer = false,
                EnableKtkOutput = true,
                MaxImages = (Configuration.MaxRenderableBrowserScreens * CurvedScreenSegments) + 1,
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
    public string SceneCompositeStatus => PctService.SceneCompositeStatus;
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
            SyncBrowserScreens();
            var activeScreen = configuration.GetActiveBrowserScreen();
            return browserScreens.TryGetValue(activeScreen.ScreenId, out var instance) ? instance : null;
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

        DrawBrowserScreens();
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

    public bool TryPlayDynamicSource(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.TryPlayDynamicSource();
    }

    public bool TryPauseDynamicSource()
    {
        return ActiveInstance?.TryPauseDynamicSource() == true;
    }

    public bool TryPauseDynamicSource(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.TryPauseDynamicSource();
    }

    public bool TrySeekDynamicSourceBy(double seconds)
    {
        return ActiveInstance?.TrySeekDynamicSourceBy(seconds) == true;
    }

    public bool TrySeekDynamicSourceTo(BrowserScreenProfile screen, double seconds)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.TrySeekDynamicSourceTo(seconds);
    }

    public bool TryRestartDynamicSource()
    {
        return ActiveInstance?.TryRestartDynamicSource() == true;
    }

    public bool TryRestartDynamicSource(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.TryRestartDynamicSource();
    }

    public bool TryShowBrowserControls(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.TryShowBrowserControls();
    }

    public bool TryHideBrowserControls(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.TryHideBrowserControls();
    }

    public bool AreBrowserControlsVisible(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.BrowserControlsVisible;
    }

    public bool AreBrowserControlsAvailable(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance) && instance.BrowserControlsAvailable;
    }

    public MediaPlaybackTelemetry? GetPlaybackTelemetry(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance)
            ? instance.PlaybackTelemetry
            : null;
    }

    public string GetSourceName(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance)
            ? instance.SourceName
            : "browser source not started";
    }

    public string GetSourceStatus(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance)
            ? instance.SourceStatus
            : "browser source not started";
    }

    public float GetDetectedVideoFps(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return browserScreens.TryGetValue(screen.ScreenId, out var instance)
            ? instance.DetectedVideoFps
            : 0.0f;
    }

    public void Dispose()
    {
        foreach (var instance in browserScreens.Values)
            instance.Dispose();
        browserScreens.Clear();
        pictomancyContext?.Dispose();
        pictomancyContext = null;
    }

    private void DrawBrowserScreens()
    {
        var screens = configuration.BrowserScreens
            .Take(Configuration.MaxRenderableBrowserScreens)
            .Where(screen => screen.Enabled)
            .Select(screen => browserScreens[screen.ScreenId]);

        DrawScreens(screens);
    }

    private void DrawScreens(IEnumerable<WorldScreenInstance> screens)
    {
        var screenList = screens.ToList();
        var prepared = new List<(WorldScreenInstance Instance, PreparedScreenTexture Texture)>();
        foreach (var screen in screenList)
        {
            if (screen.TryPrepareFrame(out var texture))
                prepared.Add((screen, texture));
        }

        if (prepared.Count == 0)
        {
            LastDrawStatus = screenList.Count switch
            {
                0 => "no enabled screens",
                1 => screenList[0].LastDrawStatus,
                _ => $"no screens ready; {screenList.Count(screen => screen.LastDrawStatus == "waiting for local player")} waiting for local player",
            };
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

#if DEBUG
        var showDebugMarker = configuration.ShowDebugMarker;
#else
        const bool showDebugMarker = false;
#endif

        foreach (var (screen, texture) in prepared)
            screen.DrawPrepared(drawList, texture, showDebugMarker);

        LastDrawStatus = $"{DescribeDrawMode(autoDraw)} drawn {prepared.Count} screen{(prepared.Count == 1 ? string.Empty : "s")}";
    }

    private void SyncBrowserScreens()
    {
        configuration.Normalize();
        var activeIds = configuration.BrowserScreens
            .Take(Configuration.MaxRenderableBrowserScreens)
            .Select(screen => screen.ScreenId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var screen in configuration.BrowserScreens.Take(Configuration.MaxRenderableBrowserScreens))
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

        foreach (var screen in configuration.BrowserScreens.Take(Configuration.MaxRenderableBrowserScreens).Where(screen => !screen.Enabled))
        {
            if (browserScreens.TryGetValue(screen.ScreenId, out var instance))
                instance.Stop();
        }
    }

    private void StopAllScreens()
    {
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
        private readonly BrowserScreenProfile browserScreen;
        private readonly DynamicVideoTexture dynamicTexture;
        private readonly SharedVideoTexture sharedTexture;
        private IVideoFrameSource? frameSource;
        private string frameSourceSignature = string.Empty;
        private long lastFrameUnixMs;
        private string lastNativeTextureError = string.Empty;
        private long lastNativeTextureErrorUnixMs;
        private long lastEffectiveAudioVolumeUnixMs;
        private long lastPauseCommandUnixMs;
        private long fallbackFrameSequence = -1_000_000_000;
        private float smoothedEffectiveAudioVolume;
        private ResolvedScreenPlacement resolvedPlacement;
        private bool hasResolvedPlacement;
        private byte[]? fallbackPixels;
        private int fallbackPixelsWidth;
        private int fallbackPixelsHeight;

        public WorldScreenInstance(Configuration configuration, BrowserScreenProfile browserScreen)
        {
            this.configuration = configuration;
            this.browserScreen = browserScreen;
            dynamicTexture = new DynamicVideoTexture(Plugin.TextureProvider);
            sharedTexture = new SharedVideoTexture();
        }

        public string SourceStatus => frameSource?.Status ?? "no dynamic source";
        public string AudioStatus => GetBrowserAudioEnabled() ? "browser audio enabled" : "browser audio muted";
        public string SourceName => frameSource?.Name ?? "no source";
        public bool BrowserControlsAvailable
        {
            get
            {
                EnsureFrameSource();
                return frameSource is IBrowserControlsHost { BrowserControlsAvailable: true };
            }
        }
        public bool BrowserControlsVisible => frameSource is IBrowserControlsHost { BrowserControlsVisible: true };
        public double LastUploadMilliseconds => sharedTexture.NativeHandle != 0
            ? sharedTexture.LastUploadMilliseconds
            : dynamicTexture.LastUploadMilliseconds;
        public long UploadCount => sharedTexture.NativeHandle != 0 ? sharedTexture.UploadCount : dynamicTexture.UploadCount;
        public int TextureWidth => sharedTexture.NativeHandle != 0 ? sharedTexture.Width : dynamicTexture.Width;
        public int TextureHeight => sharedTexture.NativeHandle != 0 ? sharedTexture.Height : dynamicTexture.Height;
        public long FrameAgeMilliseconds => lastFrameUnixMs == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs;
        public string LastDrawStatus { get; private set; } = "not drawn yet";
        public MediaPlaybackTelemetry? PlaybackTelemetry { get; private set; }
        public float AudioDistanceMeters { get; private set; }
        public float SpatialAudioAttenuation { get; private set; } = 1.0f;
        public float EffectiveAudioVolume { get; private set; }
        public float DetectedVideoFps => BrowserSourceProviderRegistry.GetDetectedVideoFps(frameSource);

        public bool TryPrepareFrame(out PreparedScreenTexture texture)
        {
            if (!IsEnabled())
            {
                LastDrawStatus = "disabled";
                Stop();
                texture = default;
                return false;
            }

            if (!TryResolvePlacement(out resolvedPlacement))
            {
                hasResolvedPlacement = false;
                LastDrawStatus = "waiting for local player";
                Stop();
                texture = default;
                return false;
            }

            hasResolvedPlacement = true;
            UpdateSpatialAudioMetrics();

            var resolvedTexture = ResolveTexture();
            if (resolvedTexture == null)
            {
                LastDrawStatus = "no texture available yet";
                texture = default;
                return false;
            }

            texture = resolvedTexture.Value;
            return true;
        }

        public void DrawPrepared(PctDrawList drawList, PreparedScreenTexture texture, bool showDebugMarker)
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
                drawList.AddText(center + new Vector3(0, panelSize.Y * 0.65f, 0), 0xFF00FFFF, browserScreen.Name, 1.0f);
            }

            var curveAmount = GetScreenCurveAmount(panelSize.X);
            var shapeStatus = curveAmount > ScreenCurveEpsilonMeters ? $"curved {curveAmount:0.##} m" : "flat";
            LastDrawStatus = TryProjectCenter(out var screen)
                ? $"drawn {shapeStatus}; center on screen at {screen.X:0}, {screen.Y:0}"
                : $"drawn {shapeStatus}; center is off-screen or behind camera";
        }

        public bool PlaceInFrontOfPlayer(float distanceMeters = 3.0f)
        {
            return ScreenPlacementResolver.PlaceInFrontOfPlayer(browserScreen.Placement, distanceMeters);
        }

        public static bool PlacePlacementInFrontOfPlayer(ScreenPlacementSettings placement, float distanceMeters = 3.0f)
        {
            return ScreenPlacementResolver.PlaceInFrontOfPlayer(placement, distanceMeters);
        }

        public bool TryProjectCenter(out Vector2 screenPosition)
        {
            if (!TryGetResolvedPlacement(out var placement))
            {
                screenPosition = default;
                return false;
            }

            return Plugin.GameGui.WorldToScreen(placement.Position, out screenPosition);
        }

        public bool TryPlayDynamicSource()
        {
            SetPlaybackPaused(false);
            lastPauseCommandUnixMs = 0;
            if (frameSource is not IMediaPlaybackController controller)
                return false;

            controller.Play();
            return true;
        }

        public bool TryPauseDynamicSource()
        {
            SetPlaybackPaused(true);
            return TrySendPauseCommand(force: true);
        }

        public bool TrySeekDynamicSourceBy(double seconds)
        {
            if (frameSource is not IMediaPlaybackController controller)
                return false;

            controller.SeekBy(seconds);
            return true;
        }

        public bool TrySeekDynamicSourceTo(double seconds)
        {
            if (frameSource is not IMediaPlaybackController controller)
                return false;

            controller.SeekTo(seconds);
            return true;
        }

        public bool TryRestartDynamicSource()
        {
            SetPlaybackPaused(false);
            lastPauseCommandUnixMs = 0;
            if (frameSource is not IMediaPlaybackController controller)
                return false;

            controller.Restart();
            return true;
        }

        public bool TryShowBrowserControls()
        {
            EnsureFrameSource();
            if (frameSource is not IBrowserControlsHost controlsHost)
                return false;

            return controlsHost.ShowBrowserControls();
        }

        public bool TryHideBrowserControls()
        {
            if (frameSource is not IBrowserControlsHost controlsHost)
                return false;

            return controlsHost.HideBrowserControls();
        }

        public void Stop()
        {
            TryHideBrowserControls();
            frameSource?.Stop();
            ResetEffectiveAudioVolume();
            PlaybackTelemetry = null;
            hasResolvedPlacement = false;
        }

        public void Dispose()
        {
            frameSource?.Dispose();
            frameSource = null;
            dynamicTexture.Dispose();
            sharedTexture.Dispose();
        }

        private PreparedScreenTexture? ResolveTexture()
        {
            EnsureFrameSource();
            if (frameSource == null)
            {
                PlaybackTelemetry = null;
                return ResolveFallbackTexture();
            }

            ApplyLiveSourceSettings();
            if (IsPlaybackPaused())
            {
                frameSource.Stop();
                TrySendPauseCommand(force: false);
                CalculateEffectiveAudioVolume(0.0f, smooth: false);
                UpdatePlaybackTelemetry();
                return ResolveCurrentTexture() ?? ResolveFallbackTexture();
            }

            frameSource.Start();
            UpdatePlaybackTelemetry();

            NativeVideoFrame? nativeFrame = null;
            var hasNativeFrame = frameSource is INativeVideoFrameSource nativeSource
                && nativeSource.TryGetLatestNativeFrame(out nativeFrame);
            var hasByteFrame = frameSource.TryGetLatestFrame(out var frame);

            if (hasNativeFrame && nativeFrame != null && (!hasByteFrame || nativeFrame.Sequence >= frame.Sequence))
            {
                try
                {
                    if (sharedTexture.Upload(nativeFrame))
                    {
                        lastFrameUnixMs = nativeFrame.TimestampUnixMs;
                        lastNativeTextureError = string.Empty;
                    }

                    if (sharedTexture.NativeHandle != 0)
                        return new PreparedScreenTexture(sharedTexture.NativeHandle, sharedTexture.Width, sharedTexture.Height);
                }
                catch (Exception ex)
                {
                    sharedTexture.Dispose();
                    lastNativeTextureError = ex.GetBaseException().Message;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now - lastNativeTextureErrorUnixMs >= 5000)
                    {
                        lastNativeTextureErrorUnixMs = now;
                        Plugin.Log.Warning(ex, "Failed to open CrystalCast shared video texture.");
                    }
                }
            }

            if (hasByteFrame)
            {
                sharedTexture.Dispose();
                if (dynamicTexture.Upload(frame))
                    lastFrameUnixMs = frame.TimestampUnixMs;
            }

            return ToPreparedTexture(dynamicTexture.TextureWrap) ?? ResolveFallbackTexture();
        }

        private PreparedScreenTexture? ResolveCurrentTexture()
        {
            if (sharedTexture.NativeHandle != 0)
                return new PreparedScreenTexture(sharedTexture.NativeHandle, sharedTexture.Width, sharedTexture.Height);

            return ToPreparedTexture(dynamicTexture.TextureWrap);
        }

        private PreparedScreenTexture? ResolveFallbackTexture()
        {
            var width = Math.Clamp(GetBrowserWidth(), 320, 3840);
            var height = Math.Clamp(GetBrowserHeight(), 180, 2160);
            if (dynamicTexture.TextureWrap != null && dynamicTexture.Width == width && dynamicTexture.Height == height)
                return ToPreparedTexture(dynamicTexture.TextureWrap);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var frame = new VideoFrame(GetFallbackPixels(width, height), width, height, --fallbackFrameSequence, now);
            if (dynamicTexture.Upload(frame))
                lastFrameUnixMs = now;

            return ToPreparedTexture(dynamicTexture.TextureWrap);
        }

        private static PreparedScreenTexture? ToPreparedTexture(IDalamudTextureWrap? texture)
        {
            if (texture == null || texture.Handle.Handle == 0)
                return null;

            return new PreparedScreenTexture((nint)texture.Handle.Handle, texture.Width, texture.Height);
        }

        private byte[] GetFallbackPixels(int width, int height)
        {
            if (fallbackPixels != null && fallbackPixelsWidth == width && fallbackPixelsHeight == height)
                return fallbackPixels;

            fallbackPixels = new byte[width * height * 4];
            for (var i = 0; i < fallbackPixels.Length; i += 4)
                fallbackPixels[i + 3] = 0xFF;

            fallbackPixelsWidth = width;
            fallbackPixelsHeight = height;
            return fallbackPixels;
        }

        private void EnsureFrameSource()
        {
            var signature = BuildFrameSourceSignature();
            if (signature == frameSourceSignature)
                return;

            frameSource?.Dispose();
            frameSource = null;
            frameSourceSignature = signature;
            dynamicTexture.Dispose();
            sharedTexture.Dispose();
            lastFrameUnixMs = 0;
            lastNativeTextureError = string.Empty;
            lastNativeTextureErrorUnixMs = 0;
            lastPauseCommandUnixMs = 0;

            frameSource = BrowserSourceProviderRegistry.CreateFrameSource(browserScreen, configuration.YouTubeBrowserEngine);
        }

        private bool TrySendPauseCommand(bool force)
        {
            if (frameSource is not IMediaPlaybackController controller)
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!force && now - lastPauseCommandUnixMs < 1000)
                return true;

            controller.Pause();
            lastPauseCommandUnixMs = now;
            return true;
        }

        private string BuildFrameSourceSignature()
        {
            return BrowserSourceProviderRegistry.BuildFrameSourceSignature(
                browserScreen,
                configuration.YouTubeBrowserEngine);
        }

        private void ApplyLiveSourceSettings()
        {
            if (frameSource is not IMediaPlaybackController controller)
                return;

            var audioEnabled = GetBrowserAudioEnabled();
            var effectiveVolume = audioEnabled
                ? CalculateEffectiveAudioVolume(GetBrowserVolume())
                : CalculateEffectiveAudioVolume(0.0f, smooth: false);
            controller.ApplyPlaybackSettings(
                audioEnabled,
                effectiveVolume,
                GetBrowserPlaybackRate(),
                GetBrowserLoop(),
                GetBrowserPlaylistAutoplayNext());

            ApplyBrowserCaptureFps();
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

        private void DrawScreenImage(PctDrawList drawList, PreparedScreenTexture texture, Vector3 center, Vector3 rightAxis, Vector3 down, Vector2 panelSize, PctDxParams p)
        {
            var curveAmount = GetScreenCurveAmount(panelSize.X);
            if (curveAmount <= ScreenCurveEpsilonMeters)
            {
                drawList.AddImage(texture.NativeHandle, center, rightAxis * panelSize.X, down, p);
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
                    texture.NativeHandle,
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
            var placement = browserScreen.Placement;
            return new PctDxParams
            {
                OccludedAlpha = Math.Clamp(placement.OccludedAlpha, 0.0f, 1.0f),
                OcclusionTolerance = Math.Max(0.0f, placement.OcclusionTolerance),
                FadeStart = GetDistanceFadeEnabled() ? Math.Max(0.0f, placement.FadeStartMeters) : float.PositiveInfinity,
                FadeStop = GetDistanceFadeEnabled()
                    ? Math.Max(placement.FadeStartMeters + 0.01f, placement.FadeStopMeters)
                    : float.PositiveInfinity,
                ProjectionHeight = 0.0f,
            };
        }

        private Vector2 GetPanelSize(PreparedScreenTexture texture)
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

        private int GetBrowserWidth()
        {
            return BrowserSourceProviderRegistry.GetDimensions(browserScreen).Width;
        }

        private int GetBrowserHeight()
        {
            return BrowserSourceProviderRegistry.GetDimensions(browserScreen).Height;
        }

        private bool GetBrowserAudioEnabled()
        {
            return BrowserSourceProviderRegistry.GetRuntimeSettings(browserScreen).AudioEnabled;
        }

        private float GetBrowserVolume()
        {
            return BrowserSourceProviderRegistry.GetRuntimeSettings(browserScreen).Volume;
        }

        private float GetBrowserPlaybackRate()
        {
            return BrowserSourceProviderRegistry.GetRuntimeSettings(browserScreen).PlaybackRate;
        }

        private bool GetBrowserLoop()
        {
            return BrowserSourceProviderRegistry.GetRuntimeSettings(browserScreen).Loop;
        }

        private bool GetBrowserPlaylistAutoplayNext()
        {
            return BrowserSourceProviderRegistry.GetRuntimeSettings(browserScreen).PlaylistAutoplayNext;
        }

        private void ApplyBrowserCaptureFps()
        {
            BrowserSourceProviderRegistry.ApplyCaptureFps(frameSource, browserScreen);
        }

        private bool IsEnabled() => browserScreen.Enabled;
        private bool IsPlaybackPaused() => browserScreen.PlaybackPaused;

        private void SetPlaybackPaused(bool paused)
        {
            browserScreen.PlaybackPaused = paused;
        }

        private bool IsSpatialAudioEnabled() => browserScreen.SpatialAudioEnabled;
        private float GetSpatialFullVolumeRadiusMeters() => browserScreen.SpatialAudioFullVolumeRadiusMeters;
        private float GetSpatialSilentRadiusMeters() => browserScreen.SpatialAudioSilentRadiusMeters;
        private bool GetDistanceFadeEnabled() => browserScreen.Placement.EnableDistanceFade;
        private float GetWidthMeters() => browserScreen.Placement.WidthMeters;
        private float GetHeightMeters() => browserScreen.Placement.HeightMeters;
        private float GetScreenCurveAmountMeters() => browserScreen.Placement.ScreenCurveAmountMeters;

        private ScreenPlacementSettings GetPlacementSettings()
        {
            return browserScreen.Placement;
        }

        private bool TryResolvePlacement(out ResolvedScreenPlacement placement)
        {
            return ScreenPlacementResolver.TryResolve(GetPlacementSettings(), GetFollowPredictionFrames(), out placement);
        }

        private float GetFollowPredictionFrames()
        {
            if (GetPlacementSettings().Mode == ScreenPlacementMode.World)
                return 0.0f;

            return configuration.OutputMode switch
            {
                Configuration.OutputModeNativeOverlay => 1.0f,
                Configuration.OutputModeSceneComposite or 3 => 1.0f,
                _ => 0.0f,
            };
        }

        private bool TryGetResolvedPlacement(out ResolvedScreenPlacement placement)
        {
            if (hasResolvedPlacement)
            {
                placement = resolvedPlacement;
                return true;
            }

            return TryResolvePlacement(out placement);
        }

        private Vector3 GetCenter()
        {
            return TryGetResolvedPlacement(out var placement) ? placement.Position : Vector3.Zero;
        }

        private Quaternion GetRotation()
        {
            return TryGetResolvedPlacement(out var placement) ? placement.Rotation : Quaternion.Identity;
        }
    }
}
