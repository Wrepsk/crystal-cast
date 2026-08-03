using System.Numerics;
using CrystalCast.Video;
using Dalamud.Interface.Textures.TextureWraps;
using Pictomancy;

namespace CrystalCast.Rendering;

public sealed class WorldScreenManager : IDisposable
{
    private const long GraphicsRetryDelayMs = 5000;

    private readonly record struct PreparedScreenTexture(nint NativeHandle, int Width, int Height);

    private readonly Configuration configuration;
    private readonly CrystalCastServices services;
    private readonly Dictionary<string, WorldScreenInstance> browserScreens = new(StringComparer.Ordinal);
    private PctContext? pictomancyContext;
    private long lastGraphicsInitializationAttemptUnixMs;
    private long lastGlobalDrawErrorUnixMs;

    internal WorldScreenManager(Configuration configuration, CrystalCastServices services)
    {
        this.configuration = configuration;
        this.services = services;

        TryInitializePictomancy(force: true);
    }

    public string Status { get; private set; } = "not initialized";
    public string LastDrawStatus { get; private set; } = "not drawn yet";
    public string LastGraphicsError { get; private set; } = "none recorded";
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
    public int ActiveBrowserRuntimeCount => browserScreens.Values.Count(instance => instance.IsBrowserRuntimeActive);
    public string BrowserResourceBudgetStatus
    {
        get
        {
            var active = ScreenLimitPolicy.GetActiveScreens(configuration.BrowserScreens).Count;
            var deferred = ScreenLimitPolicy.CountDeferredActiveScreens(configuration.BrowserScreens);
            return deferred == 0
                ? $"{active}/{Configuration.MaxActiveBrowserScreens} active browser slots used"
                : $"{active}/{Configuration.MaxActiveBrowserScreens} active browser slots used; {deferred} enabled screen{(deferred == 1 ? string.Empty : "s")} deferred";
        }
    }

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
        GraphicsDiagnostics.Enabled = configuration.EnableGpuDiagnostics;
        SyncBrowserScreens();

        if (pictomancyContext == null)
        {
            TryInitializePictomancy(force: false);
        }

        if (pictomancyContext == null)
        {
            LastDrawStatus = "Pictomancy is not initialized";
            ReleaseAllBrowserRuntimes();
            return;
        }

        if (!configuration.Enabled)
        {
            LastDrawStatus = "disabled";
            ReleaseAllBrowserRuntimes();
            return;
        }

        try
        {
            DrawBrowserScreens();
        }
        catch (Exception ex) when (NativeGraphicsError.IsDeviceLost(ex))
        {
            HandleGraphicsDeviceLoss(ex);
        }
        catch (Exception ex)
        {
            LastDrawStatus = $"draw pipeline failed: {ex.GetBaseException().Message}";
            LastGraphicsError = DescribeDiagnosticException(ex);
            LogGlobalDrawFailure(ex);
        }
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
            : services.PlacementResolver.PlaceInFrontOfPlayer(screen.Placement, distanceMeters);
    }

    public bool TryPlayDynamicSource()
    {
        var screen = configuration.GetActiveBrowserScreen();
        return IsWithinBrowserResourceBudget(screen) && ActiveInstance?.TryPlayDynamicSource() == true;
    }

    public bool TryPlayDynamicSource(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return IsWithinBrowserResourceBudget(screen)
            && browserScreens.TryGetValue(screen.ScreenId, out var instance)
            && instance.TryPlayDynamicSource();
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
        var screen = configuration.GetActiveBrowserScreen();
        return IsWithinBrowserResourceBudget(screen) && ActiveInstance?.TryRestartDynamicSource() == true;
    }

    public bool TryRestartDynamicSource(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return IsWithinBrowserResourceBudget(screen)
            && browserScreens.TryGetValue(screen.ScreenId, out var instance)
            && instance.TryRestartDynamicSource();
    }

    public bool TryShowBrowserControls(BrowserScreenProfile screen)
    {
        SyncBrowserScreens();
        return IsWithinBrowserResourceBudget(screen)
            && browserScreens.TryGetValue(screen.ScreenId, out var instance)
            && instance.TryShowBrowserControls();
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
        return IsWithinBrowserResourceBudget(screen)
            && browserScreens.TryGetValue(screen.ScreenId, out var instance)
            && instance.BrowserControlsAvailable;
    }

    private bool IsWithinBrowserResourceBudget(BrowserScreenProfile screen)
    {
        return configuration.Enabled
            && screen.Enabled
            && ScreenLimitPolicy.GetActiveScreens(configuration.BrowserScreens)
            .Any(active => string.Equals(active.ScreenId, screen.ScreenId, StringComparison.Ordinal));
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

    internal IReadOnlyList<WorldScreenDiagnosticSnapshot> GetDiagnosticSnapshots()
    {
        SyncBrowserScreens();
        var activeScreenIds = ScreenLimitPolicy.GetActiveScreens(configuration.BrowserScreens)
            .Select(screen => screen.ScreenId)
            .ToHashSet(StringComparer.Ordinal);

        return configuration.BrowserScreens.Select((screen, index) =>
        {
            var source = BrowserSourceProviderRegistry.GetSnapshot(screen);
            browserScreens.TryGetValue(screen.ScreenId, out var instance);
            var captureMode = BrowserPlatformPolicy.ResolveCaptureMode(configuration.YouTubeBrowserEngine, WineEnvironment.IsWine);
            var texturePipeline = instance == null
                ? "not initialized"
                : captureMode == WebView2CaptureMode.WindowGraphicsCapture
                    ? instance.TextureWidth > 0 ? "shared GPU texture" : "shared GPU texture pending"
                    : instance.TextureWidth > 0 ? "CPU frame upload" : "CPU frame upload pending";
            var gpuSampleStatus = captureMode == WebView2CaptureMode.WindowGraphicsCapture
                ? instance?.GpuSampleStatus ?? "game texture sample pending"
                : "not applicable to JPEG capture";

            return new WorldScreenDiagnosticSnapshot(
                index + 1,
                string.Equals(screen.ScreenId, configuration.ActiveBrowserScreenId, StringComparison.Ordinal),
                screen.Enabled,
                activeScreenIds.Contains(screen.ScreenId),
                screen.CreatedByIpc,
                screen.PlaybackPaused,
                screen.ProviderKind,
                screen.Placement.Mode,
                !string.IsNullOrWhiteSpace(source.Url),
                source.Dimensions.Width,
                source.Dimensions.Height,
                source.CaptureSettings.FramesPerSecond,
                source.CaptureSettings.Manual,
                captureMode,
                instance?.HasBrowserRuntime == true,
                instance?.IsBrowserRuntimeActive == true,
                instance?.BrowserControlsVisible == true,
                instance?.SourceName ?? "browser source not started",
                instance?.SourceStatus ?? "browser source not started",
                instance?.LastDrawStatus ?? "not initialized",
                instance?.LastError ?? "none recorded",
                texturePipeline,
                gpuSampleStatus,
                instance?.TextureWidth ?? 0,
                instance?.TextureHeight ?? 0,
                instance?.UploadCount ?? 0,
                instance?.LastUploadMilliseconds ?? 0.0,
                instance?.FrameAgeMilliseconds ?? 0,
                screen.Placement.WidthMeters,
                screen.Placement.HeightMeters,
                screen.Placement.ScreenCurveAmountMeters,
                screen.Placement.OccludedAlpha,
                screen.Placement.EnableDistanceFade);
        }).ToArray();
    }

    public void Dispose()
    {
        foreach (var instance in browserScreens.Values)
        {
            try
            {
                instance.Dispose();
            }
            catch (Exception ex)
            {
                services.Log.Debug(ex, "Failed to dispose a CrystalCast screen instance.");
            }
        }
        browserScreens.Clear();
        try
        {
            pictomancyContext?.Dispose();
        }
        catch (Exception ex)
        {
            services.Log.Debug(ex, "Failed to dispose the CrystalCast Pictomancy context.");
        }
        pictomancyContext = null;
    }

    private void TryInitializePictomancy(bool force)
    {
        if (pictomancyContext != null)
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!force && now - lastGraphicsInitializationAttemptUnixMs < GraphicsRetryDelayMs)
            return;

        lastGraphicsInitializationAttemptUnixMs = now;
        try
        {
            pictomancyContext = PctService.Initialize(services.PluginInterface, new PctOptions
            {
                EnableVfxRenderer = false,
                EnableKtkOutput = true,
                MaxImages = (Configuration.MaxActiveBrowserScreens * CurvedScreenTessellation.MaxSegments) + 1,
            });
            Status = "Pictomancy ready";
        }
        catch (Exception ex)
        {
            Status = $"Pictomancy init failed: {ex.Message}";
            LastGraphicsError = DescribeDiagnosticException(ex);
            services.Log.Error(ex, "Failed to initialize Pictomancy.");
        }
    }

    private void HandleGraphicsDeviceLoss(Exception exception)
    {
        LastDrawStatus = $"graphics device lost; retrying: {exception.GetBaseException().Message}";
        Status = "graphics device lost";
        LastGraphicsError = DescribeDiagnosticException(exception);
        try
        {
            pictomancyContext?.Dispose();
        }
        catch
        {
        }
        pictomancyContext = null;
        foreach (var instance in browserScreens.Values)
        {
            try
            {
                instance.ResetGraphicsResources();
            }
            catch (Exception resetException)
            {
                services.Log.Debug(resetException, "Failed to reset a CrystalCast screen after device loss.");
            }
        }
        LogGlobalDrawFailure(exception);
    }

    private static string DescribeDiagnosticException(Exception exception)
    {
        var root = exception.GetBaseException();
        return $"{DateTimeOffset.UtcNow:O}; {root.GetType().Name}; HRESULT 0x{root.HResult:X8}; {root.Message}";
    }

    private void LogGlobalDrawFailure(Exception exception)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - lastGlobalDrawErrorUnixMs < GraphicsRetryDelayMs)
            return;

        lastGlobalDrawErrorUnixMs = now;
        services.Log.Warning(exception, "CrystalCast graphics pipeline failed; rendering will retry.");
    }

    private void DrawBrowserScreens()
    {
        var screens = ScreenLimitPolicy.GetActiveScreens(configuration.BrowserScreens)
            .Select(screen => browserScreens[screen.ScreenId]);

        DrawScreens(screens);
        var deferred = ScreenLimitPolicy.CountDeferredActiveScreens(configuration.BrowserScreens);
        if (deferred > 0)
            LastDrawStatus += $"; {deferred} enabled screen{(deferred == 1 ? string.Empty : "s")} deferred by the {Configuration.MaxActiveBrowserScreens}-browser resource budget";
    }

    private void DrawScreens(IEnumerable<WorldScreenInstance> screens)
    {
        var screenList = screens.ToList();
        var prepared = new List<(WorldScreenInstance Instance, PreparedScreenTexture Texture)>();
        foreach (var screen in screenList)
        {
            try
            {
                if (screen.TryPrepareFrame(out var texture))
                    prepared.Add((screen, texture));
            }
            catch (Exception ex)
            {
                screen.RecordDrawFailure(ex, "prepare");
            }
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

        var drawnCount = 0;
        foreach (var (screen, texture) in prepared)
        {
            try
            {
                screen.DrawPrepared(drawList, texture, showDebugMarker);
                drawnCount++;
            }
            catch (Exception ex)
            {
                screen.RecordDrawFailure(ex, "draw");
            }
        }

        LastDrawStatus = $"{DescribeDrawMode(autoDraw)} drawn {drawnCount}/{prepared.Count} prepared screen{(prepared.Count == 1 ? string.Empty : "s")}";
    }

    private void SyncBrowserScreens()
    {
        configuration.Normalize();
        var allowedScreens = ScreenLimitPolicy.GetAllowedScreens(configuration.BrowserScreens);
        var activeIds = allowedScreens
            .Select(screen => screen.ScreenId)
            .ToHashSet(StringComparer.Ordinal);
        var runningIds = BrowserRuntimeRetentionPolicy.GetRetainedScreenIds(configuration.Enabled, allowedScreens);

        foreach (var screen in allowedScreens)
        {
            if (browserScreens.TryGetValue(screen.ScreenId, out var instance))
                instance.UpdateProfile(screen);
            else
                browserScreens[screen.ScreenId] = new WorldScreenInstance(configuration, screen, services);
        }

        foreach (var (screenId, instance) in browserScreens.ToArray())
        {
            if (activeIds.Contains(screenId))
                continue;

            try
            {
                instance.Dispose();
            }
            catch (Exception ex)
            {
                services.Log.Debug(ex, "Failed to dispose CrystalCast screen {ScreenId}.", screenId);
            }
            browserScreens.Remove(screenId);
        }

        foreach (var screen in allowedScreens.Where(screen => !runningIds.Contains(screen.ScreenId)))
        {
            if (browserScreens.TryGetValue(screen.ScreenId, out var instance))
                TryReleaseBrowserRuntime(screen.ScreenId, instance);
        }
    }

    private void ReleaseAllBrowserRuntimes()
    {
        foreach (var (screenId, instance) in browserScreens)
            TryReleaseBrowserRuntime(screenId, instance);
    }

    private void TryReleaseBrowserRuntime(string screenId, WorldScreenInstance instance)
    {
        try
        {
            instance.ReleaseBrowserRuntime();
        }
        catch (Exception ex)
        {
            services.Log.Debug(ex, "Failed to release the browser runtime for CrystalCast screen {ScreenId}.", screenId);
        }
    }

    private AutoDraw GetAutoDraw()
    {
        return configuration.OutputMode switch
        {
            ScreenOutputMode.NativeOverlay => AutoDraw.NativeOverlay,
            ScreenOutputMode.SceneComposite => AutoDraw.SceneComposite,
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
        private BrowserScreenProfile browserScreen;
        private readonly CrystalCastServices services;
        private readonly DynamicVideoTexture dynamicTexture;
        private readonly SharedVideoTexture sharedTexture;
        private IVideoFrameSource? frameSource;
        private string frameSourceSignature = string.Empty;
        private long lastFrameUnixMs;
        private string lastNativeTextureError = string.Empty;
        private long lastNativeTextureErrorUnixMs;
        private long lastDrawFailureLogUnixMs;
        private long lastEffectiveAudioVolumeUnixMs;
        private long lastPauseCommandUnixMs;
        private MediaPlaybackTelemetry? playbackTelemetry;
        private long fallbackFrameSequence = -1_000_000_000;
        private float smoothedEffectiveAudioVolume;
        private ResolvedScreenPlacement resolvedPlacement;
        private bool hasResolvedPlacement;
        private byte[]? fallbackPixels;
        private int fallbackPixelsWidth;
        private int fallbackPixelsHeight;

        public WorldScreenInstance(Configuration configuration, BrowserScreenProfile browserScreen, CrystalCastServices services)
        {
            this.configuration = configuration;
            this.browserScreen = browserScreen;
            this.services = services;
            dynamicTexture = new DynamicVideoTexture(services.TextureProvider);
            sharedTexture = new SharedVideoTexture();
        }

        public void UpdateProfile(BrowserScreenProfile profile)
        {
            if (!string.Equals(browserScreen.ScreenId, profile.ScreenId, StringComparison.Ordinal))
                throw new ArgumentException("Cannot bind a renderer instance to a different screen ID.", nameof(profile));

            browserScreen = profile;
        }

        public string SourceStatus => frameSource?.Status ?? "no dynamic source";
        public string AudioStatus => BrowserSourceProviderRegistry.GetSnapshot(browserScreen).RuntimeSettings.AudioEnabled
            ? "browser audio enabled"
            : "browser audio muted";
        public string SourceName => frameSource?.Name ?? "no source";
        public bool HasBrowserRuntime => frameSource != null;
        public bool IsBrowserRuntimeActive => frameSource?.IsRunning == true;
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
        public string LastError { get; private set; } = "none recorded";
        public string GpuSampleStatus => sharedTexture.DiagnosticStatus;
        public MediaPlaybackTelemetry? PlaybackTelemetry => Volatile.Read(ref playbackTelemetry);
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

        public void RecordDrawFailure(Exception exception, string stage)
        {
            LastDrawStatus = $"{stage} failed: {exception.GetBaseException().Message}";
            LastError = DescribeDiagnosticException(exception);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - lastDrawFailureLogUnixMs < 5000)
                return;

            lastDrawFailureLogUnixMs = now;
            services.Log.Warning(exception, "CrystalCast screen {ScreenId} failed during {Stage}; other screens will continue.", browserScreen.ScreenId, stage);
        }

        public bool PlaceInFrontOfPlayer(float distanceMeters = 3.0f)
        {
            return services.PlacementResolver.PlaceInFrontOfPlayer(browserScreen.Placement, distanceMeters);
        }

        public bool TryProjectCenter(out Vector2 screenPosition)
        {
            if (!TryGetResolvedPlacement(out var placement))
            {
                screenPosition = default;
                return false;
            }

            return services.GameGui.WorldToScreen(placement.Position, out screenPosition);
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
            Volatile.Write(ref playbackTelemetry, null);
            hasResolvedPlacement = false;
        }

        public void ReleaseBrowserRuntime()
        {
            if (frameSource == null && string.IsNullOrEmpty(frameSourceSignature))
                return;

            var source = frameSource;
            frameSource = null;
            frameSourceSignature = string.Empty;

            try
            {
                source?.Dispose();
            }
            catch (Exception ex)
            {
                services.Log.Debug(ex, "Failed to dispose the browser frame source for screen {ScreenId}.", browserScreen.ScreenId);
            }

            try
            {
                dynamicTexture.Dispose();
            }
            catch (Exception ex)
            {
                services.Log.Debug(ex, "Failed to dispose the dynamic browser texture for screen {ScreenId}.", browserScreen.ScreenId);
            }

            try
            {
                sharedTexture.Dispose();
            }
            catch (Exception ex)
            {
                services.Log.Debug(ex, "Failed to dispose the shared browser texture for screen {ScreenId}.", browserScreen.ScreenId);
            }

            fallbackPixels = null;
            fallbackPixelsWidth = 0;
            fallbackPixelsHeight = 0;
            lastFrameUnixMs = 0;
            lastNativeTextureError = string.Empty;
            lastNativeTextureErrorUnixMs = 0;
            lastPauseCommandUnixMs = 0;
            ResetEffectiveAudioVolume();
            Volatile.Write(ref playbackTelemetry, null);
            hasResolvedPlacement = false;
        }

        public void Dispose()
        {
            try
            {
                ReleaseBrowserRuntime();
            }
            finally
            {
                try
                {
                    dynamicTexture.Dispose();
                }
                finally
                {
                    sharedTexture.Dispose();
                }
            }
        }

        public void ResetGraphicsResources()
        {
            dynamicTexture.Dispose();
            sharedTexture.Dispose();
            lastFrameUnixMs = 0;
            lastNativeTextureError = string.Empty;
            lastNativeTextureErrorUnixMs = 0;
        }

        private PreparedScreenTexture? ResolveTexture()
        {
            EnsureFrameSource();
            if (frameSource == null)
            {
                Volatile.Write(ref playbackTelemetry, null);
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
            try
            {
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
                            services.Log.Warning(ex, "Failed to open CrystalCast shared video texture.");
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
            finally
            {
                if (hasByteFrame)
                    frame.Dispose();
            }
        }

        private PreparedScreenTexture? ResolveCurrentTexture()
        {
            if (sharedTexture.NativeHandle != 0)
                return new PreparedScreenTexture(sharedTexture.NativeHandle, sharedTexture.Width, sharedTexture.Height);

            return ToPreparedTexture(dynamicTexture.TextureWrap);
        }

        private PreparedScreenTexture? ResolveFallbackTexture()
        {
            var dimensions = BrowserSourceProviderRegistry.GetSnapshot(browserScreen).Dimensions;
            var width = Math.Clamp(dimensions.Width, 320, 3840);
            var height = Math.Clamp(dimensions.Height, 180, 2160);
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

            try
            {
                frameSource?.Dispose();
            }
            catch (Exception ex)
            {
                services.Log.Debug(ex, "Failed to dispose the previous frame source for screen {ScreenId}.", browserScreen.ScreenId);
            }
            frameSource = null;
            frameSourceSignature = signature;
            dynamicTexture.Dispose();
            sharedTexture.Dispose();
            lastFrameUnixMs = 0;
            lastNativeTextureError = string.Empty;
            lastNativeTextureErrorUnixMs = 0;
            lastPauseCommandUnixMs = 0;

            frameSource = BrowserSourceProviderRegistry.CreateFrameSource(
                browserScreen,
                configuration.YouTubeBrowserEngine,
                services.BrowserFrameSourceFactory);
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

            var runtime = BrowserSourceProviderRegistry.GetSnapshot(browserScreen).RuntimeSettings;
            var audioEnabled = runtime.AudioEnabled;
            var effectiveVolume = audioEnabled
                ? CalculateEffectiveAudioVolume(runtime.Volume)
                : CalculateEffectiveAudioVolume(0.0f, smooth: false);
            controller.ApplyPlaybackSettings(
                audioEnabled,
                effectiveVolume,
                runtime.PlaybackRate,
                runtime.Loop,
                runtime.PlaylistAutoplayNext);

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

            var player = services.ObjectTable.LocalPlayer;
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
            var segmentCount = CurvedScreenTessellation.GetSegmentCount(curve.HalfAngle);
            for (var i = 0; i < segmentCount; i++)
            {
                var u0 = (float)i / segmentCount;
                var u1 = (float)(i + 1) / segmentCount;
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
            var segmentCount = CurvedScreenTessellation.GetSegmentCount(curve.HalfAngle);

            for (var i = 0; i < segmentCount; i++)
            {
                var u0 = (float)i / segmentCount;
                var u1 = (float)(i + 1) / segmentCount;
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
                Volatile.Write(ref playbackTelemetry, telemetry);
                return;
            }

            Volatile.Write(ref playbackTelemetry, null);
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
            return ScreenPanelSizeResolver.Resolve(GetPlacementSettings(), texture.Width, texture.Height);
        }

        private Vector2 GetPanelSizeForSource()
        {
            return ScreenPanelSizeResolver.Resolve(
                GetPlacementSettings(),
                frameSource?.Width ?? 0,
                frameSource?.Height ?? 0);
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
            return services.PlacementResolver.TryResolve(GetPlacementSettings(), GetFollowPredictionFrames(), out placement);
        }

        private float GetFollowPredictionFrames()
        {
            if (GetPlacementSettings().Mode == ScreenPlacementMode.World)
                return 0.0f;

            return configuration.OutputMode switch
            {
                ScreenOutputMode.NativeOverlay => 1.0f,
                ScreenOutputMode.SceneComposite => 1.0f,
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

internal sealed record WorldScreenDiagnosticSnapshot(
    int Number,
    bool IsSelected,
    bool Enabled,
    bool WithinResourceBudget,
    bool CreatedByIpc,
    bool PlaybackPaused,
    BrowserSourceProviderKind Provider,
    ScreenPlacementMode PlacementMode,
    bool SourceConfigured,
    int ConfiguredWidth,
    int ConfiguredHeight,
    float ConfiguredCaptureFps,
    bool CaptureFpsManual,
    WebView2CaptureMode CaptureMode,
    bool RuntimeCreated,
    bool CaptureRunning,
    bool BrowserControlsVisible,
    string SourceName,
    string SourceStatus,
    string DrawStatus,
    string LastError,
    string TexturePipeline,
    string GpuSampleStatus,
    int TextureWidth,
    int TextureHeight,
    long UploadCount,
    double LastUploadMilliseconds,
    long FrameAgeMilliseconds,
    float WidthMeters,
    float HeightMeters,
    float CurveMeters,
    float OccludedAlpha,
    bool DistanceFadeEnabled);
