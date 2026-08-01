namespace CrystalCast.Video;

internal sealed class BrowserFrameSource : IVideoFrameSource, INativeVideoFrameSource, IMediaPlaybackTelemetrySource, IMediaPlaybackController, IBrowserFrameSourceRuntime, IBrowserControlsHost
{
    private readonly BrowserSourceDescriptor descriptor;
    private readonly string input;
    private readonly int width;
    private readonly int height;
    private readonly float captureFps;
    private readonly BrowserMediaEngine enginePreference;
    private readonly bool autoplay;
    private bool currentLoop;
    private bool currentPlaylistAutoplayNext;
    private bool currentAudioEnabled;
    private float currentVolume;
    private float currentPlaybackRate;
    private IVideoFrameSource? activeSource;
    private BrowserMediaEngine activeEngine;
    private string fallbackStatus = string.Empty;

    public BrowserFrameSource(
        BrowserSourceDescriptor descriptor,
        string input,
        int width,
        int height,
        float captureFps,
        BrowserMediaEngine enginePreference,
        bool autoplay,
        bool loop,
        bool playlistAutoplayNext,
        bool audioEnabled,
        float volume,
        float playbackRate)
    {
        this.descriptor = descriptor;
        this.input = input;
        this.width = width;
        this.height = height;
        this.captureFps = captureFps;
        this.enginePreference = enginePreference;
        this.autoplay = autoplay;
        currentLoop = loop;
        currentPlaylistAutoplayNext = playlistAutoplayNext;
        currentAudioEnabled = audioEnabled;
        currentVolume = volume;
        currentPlaybackRate = playbackRate;
    }

    public BrowserSourceProviderKind ProviderKind => descriptor.ProviderKind;
    public string Name => activeSource?.Name ?? $"{descriptor.DisplayName} browser ({DescribePreference(enginePreference)})";
    public int Width => activeSource?.Width ?? Math.Clamp(width, 320, 3840);
    public int Height => activeSource?.Height ?? Math.Clamp(height, 180, 2160);
    public float FramesPerSecond => activeSource?.FramesPerSecond ?? Math.Clamp(captureFps, 1.0f, 120.0f);
    public bool IsRunning => activeSource?.IsRunning ?? false;
    public string Status => string.IsNullOrWhiteSpace(fallbackStatus)
        ? activeSource?.Status ?? "browser source not started"
        : $"{activeSource?.Status ?? "browser source not started"}; {fallbackStatus}";
    public bool BrowserControlsAvailable => activeSource is IBrowserControlsHost { BrowserControlsAvailable: true }
        || (activeSource == null && CanInitialSourceUseBrowserControls());
    public bool BrowserControlsVisible => activeSource is IBrowserControlsHost { BrowserControlsVisible: true };

    public float DetectedVideoFps
    {
        get
        {
            if (activeSource is IMediaPlaybackTelemetrySource telemetrySource && telemetrySource.TryGetPlaybackTelemetry(out var t))
                return t.DetectedVideoFps;

            return 0.0f;
        }
    }

    public void Start()
    {
        EnsureSource();
        StartActiveSource();
        TryFallbackFromCefStartFailure();
    }

    public void Stop()
    {
        activeSource?.Stop();
    }

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        TryFallbackFromCefPlayerFailure();

        if (activeSource != null)
            return activeSource.TryGetLatestFrame(out frame);

        frame = null!;
        return false;
    }

    public bool TryGetLatestNativeFrame(out NativeVideoFrame frame)
    {
        TryFallbackFromCefPlayerFailure();

        if (activeSource is INativeVideoFrameSource nativeSource)
            return nativeSource.TryGetLatestNativeFrame(out frame);

        frame = null!;
        return false;
    }

    public bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry telemetry)
    {
        if (activeSource is IMediaPlaybackTelemetrySource telemetrySource)
            return telemetrySource.TryGetPlaybackTelemetry(out telemetry);

        telemetry = new MediaPlaybackTelemetry();
        return false;
    }

    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        currentAudioEnabled = audioEnabled;
        currentVolume = volume;
        currentPlaybackRate = playbackRate;
        currentLoop = loop;
        currentPlaylistAutoplayNext = playlistAutoplayNext;

        if (activeSource is IMediaPlaybackController controller)
            controller.ApplyPlaybackSettings(audioEnabled, volume, playbackRate, loop, playlistAutoplayNext);
    }

    public void Play()
    {
        EnsureSource();
        if (activeSource is IMediaPlaybackController controller)
            controller.Play();
    }

    public void Pause()
    {
        if (activeSource is IMediaPlaybackController controller)
            controller.Pause();
    }

    public void SeekBy(double seconds)
    {
        if (activeSource is IMediaPlaybackController controller)
            controller.SeekBy(seconds);
    }

    public void SeekTo(double seconds)
    {
        if (activeSource is IMediaPlaybackController controller)
            controller.SeekTo(seconds);
    }

    public void Restart()
    {
        EnsureSource();
        if (activeSource is IMediaPlaybackController controller)
            controller.Restart();
        TryFallbackFromCefStartFailure();
    }

    public void UpdateCaptureFps(float fps)
    {
        if (activeSource is IBrowserFrameSourceRuntime runtime)
            runtime.UpdateCaptureFps(fps);
    }

    public bool ShowBrowserControls()
    {
        EnsureSource();
        return activeSource is IBrowserControlsHost controlsHost && controlsHost.ShowBrowserControls();
    }

    public bool HideBrowserControls()
    {
        return activeSource is IBrowserControlsHost controlsHost && controlsHost.HideBrowserControls();
    }

    public void Dispose()
    {
        activeSource?.Dispose();
        activeSource = null;
    }

    private void EnsureSource()
    {
        if (activeSource != null)
            return;

        IVideoFrameSource candidate;
        try
        {
            candidate = CreateSource();
        }
        catch (Exception ex)
        {
            candidate = CreateFallbackAfterFailure(ex);
        }

        if (BrowserCandidateLifetime.TryUse(candidate, () => ApplyCurrentSettings(candidate), out var error))
            activeSource = candidate;
        else
            activeSource = CreateUnavailableSource(error!);
    }

    private IVideoFrameSource CreateSource()
    {
        return enginePreference switch
        {
            BrowserMediaEngine.CefOffScreen => CreateCefSource(),
            BrowserMediaEngine.WebView2Capture => CreateWebView2Source(),
            BrowserMediaEngine.WebView2WindowCapture => CreateWebView2WindowCaptureSource(),
            _ => CreateAutoSource(),
        };
    }

    private bool CanInitialSourceUseBrowserControls()
    {
        return enginePreference is BrowserMediaEngine.WebView2Capture or BrowserMediaEngine.WebView2WindowCapture
            || (enginePreference == BrowserMediaEngine.Auto && descriptor.PreferredAutoEngine == BrowserMediaEngine.WebView2Capture);
    }

    private IVideoFrameSource CreateAutoSource()
    {
        return descriptor.PreferredAutoEngine == BrowserMediaEngine.WebView2Capture
            ? CreateAutoWebView2FirstSource()
            : CreateAutoCefFirstSource();
    }

    private IVideoFrameSource CreateAutoCefFirstSource()
    {
        fallbackStatus = string.Empty;
        return CreateCefSource();
    }

    private IVideoFrameSource CreateAutoWebView2FirstSource()
    {
        if (WebView2BrowserFrameSource.TryGetWebView2Runtime(out var runtimeVersion, out var webView2Status))
        {
            fallbackStatus = $"using WebView2 window capture for {descriptor.DisplayName}: {runtimeVersion}";
            return CreateWebView2WindowCaptureSource();
        }

        fallbackStatus = $"WebView2 unavailable, trying CEF for {descriptor.DisplayName}: {webView2Status}";
        return CreateCefSource();
    }

    private IVideoFrameSource CreateCefSource()
    {
        fallbackStatus = enginePreference == BrowserMediaEngine.CefOffScreen ? string.Empty : fallbackStatus;
        activeEngine = BrowserMediaEngine.CefOffScreen;
        return new CefBrowserFrameSource(descriptor, input, width, height, captureFps, autoplay, currentLoop, currentPlaylistAutoplayNext, currentAudioEnabled, currentVolume, currentPlaybackRate);
    }

    private IVideoFrameSource CreateWebView2Source()
    {
        activeEngine = BrowserMediaEngine.WebView2Capture;
        return new WebView2BrowserFrameSource(descriptor, input, width, height, captureFps, autoplay, currentLoop, currentPlaylistAutoplayNext, currentAudioEnabled, currentVolume, currentPlaybackRate);
    }

    private IVideoFrameSource CreateWebView2WindowCaptureSource()
    {
        activeEngine = BrowserMediaEngine.WebView2WindowCapture;
        return new WebView2BrowserFrameSource(
            descriptor,
            input,
            width,
            height,
            captureFps,
            autoplay,
            currentLoop,
            currentPlaylistAutoplayNext,
            currentAudioEnabled,
            currentVolume,
            currentPlaybackRate,
            WebView2CaptureMode.WindowGraphicsCapture);
    }

    private void StartActiveSource()
    {
        var candidate = activeSource;
        if (candidate == null)
            return;

        activeSource = null;
        if (BrowserCandidateLifetime.TryUse(candidate, candidate.Start, out var startError))
        {
            activeSource = candidate;
            return;
        }

        var fallbackCandidate = CreateFallbackAfterFailure(startError!);
        if (BrowserCandidateLifetime.TryUse(fallbackCandidate, () =>
            {
                ApplyCurrentSettings(fallbackCandidate);
                fallbackCandidate.Start();
            }, out var fallbackError))
        {
            activeSource = fallbackCandidate;
            return;
        }

        activeSource = CreateUnavailableSource(fallbackError!);
    }

    private IVideoFrameSource CreateFallbackAfterFailure(Exception ex)
    {
        var baseMessage = ex.GetBaseException().Message;
        Plugin.Log.Warning(ex, "Failed to start CrystalCast browser source.");

        if (activeEngine == BrowserMediaEngine.CefOffScreen && enginePreference == BrowserMediaEngine.Auto)
        {
            fallbackStatus = $"CEF failed, using WebView2 window capture fallback: {baseMessage}";
            try
            {
                return CreateWebView2WindowCaptureSource();
            }
            catch (Exception webViewEx)
            {
                Plugin.Log.Warning(webViewEx, "Failed to create CrystalCast WebView2 window capture fallback source.");
                return CreateUnavailableSource(webViewEx);
            }
        }

        return CreateUnavailableSource(ex);
    }

    private IVideoFrameSource CreateUnavailableSource(Exception ex)
    {
        return CreateUnavailableSource($"browser source unavailable: {ex.GetBaseException().Message}");
    }

    private IVideoFrameSource CreateUnavailableSource(string unavailableStatus)
    {
        activeEngine = 0;
        fallbackStatus = unavailableStatus;
        return new UnavailableFrameSource(Width, Height, FramesPerSecond, fallbackStatus);
    }

    private void TryFallbackFromCefStartFailure()
    {
        if (enginePreference != BrowserMediaEngine.Auto || activeEngine != BrowserMediaEngine.CefOffScreen || activeSource == null || activeSource.IsRunning)
            return;

        var cefStatus = activeSource.Status;
        if (cefStatus.Contains(descriptor.InvalidSourceMessage, StringComparison.OrdinalIgnoreCase))
            return;

        activeSource.Dispose();
        activeSource = null;
        var fallbackCandidate = CreateWebView2WindowCaptureSource();
        fallbackStatus = $"CEF failed, using WebView2 window capture fallback: {cefStatus}";

        if (BrowserCandidateLifetime.TryUse(fallbackCandidate, () =>
            {
                ApplyCurrentSettings(fallbackCandidate);
                fallbackCandidate.Start();
            }, out var error))
            activeSource = fallbackCandidate;
        else
            activeSource = CreateUnavailableSource(error!);
    }

    private void TryFallbackFromCefPlayerFailure()
    {
        if (enginePreference != BrowserMediaEngine.Auto
            || activeEngine != BrowserMediaEngine.CefOffScreen
            || activeSource is not CefBrowserFrameSource { HasPlayerFailed: true } cefSource)
        {
            return;
        }

        var cefStatus = cefSource.Status;
        activeSource.Dispose();
        activeSource = null;
        var fallbackCandidate = CreateWebView2WindowCaptureSource();
        fallbackStatus = $"CEF {descriptor.DisplayName} playback failed, using WebView2 window capture fallback: {cefStatus}";

        if (BrowserCandidateLifetime.TryUse(fallbackCandidate, () =>
            {
                ApplyCurrentSettings(fallbackCandidate);
                fallbackCandidate.Start();
            }, out var error))
            activeSource = fallbackCandidate;
        else
            activeSource = CreateUnavailableSource(error!);
    }

    private void ApplyCurrentSettings(IVideoFrameSource candidate)
    {
        if (candidate is IMediaPlaybackController controller)
            controller.ApplyPlaybackSettings(currentAudioEnabled, currentVolume, currentPlaybackRate, currentLoop, currentPlaylistAutoplayNext);
    }

    private static string DescribePreference(BrowserMediaEngine engine)
    {
        return engine switch
        {
            BrowserMediaEngine.CefOffScreen => "CEF offscreen",
            BrowserMediaEngine.WebView2Capture => "WebView2 JPEG capture",
            BrowserMediaEngine.WebView2WindowCapture => "WebView2 window capture",
            _ => "auto",
        };
    }

    private sealed class UnavailableFrameSource(int width, int height, float framesPerSecond, string status) : IVideoFrameSource
    {
        public string Name => "browser source (unavailable)";
        public int Width { get; } = width;
        public int Height { get; } = height;
        public float FramesPerSecond { get; } = framesPerSecond;
        public bool IsRunning => false;
        public string Status { get; } = status;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public bool TryGetLatestFrame(out VideoFrame frame)
        {
            frame = null!;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
