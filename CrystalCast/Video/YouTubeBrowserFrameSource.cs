using System.Runtime.CompilerServices;

namespace CrystalCast.Video;

public sealed class YouTubeBrowserFrameSource : IVideoFrameSource, IMediaPlaybackTelemetrySource, IMediaPlaybackController
{
    private readonly string input;
    private readonly int width;
    private readonly int height;
    private readonly float captureFps;
    private readonly BrowserMediaEngine enginePreference;
    private readonly bool autoplay;
    private bool currentLoop;
    private bool currentAudioEnabled;
    private float currentVolume;
    private float currentPlaybackRate;
    private IVideoFrameSource? activeSource;
    private BrowserMediaEngine activeEngine;
    private string fallbackStatus = string.Empty;

    public YouTubeBrowserFrameSource(
        string input,
        int width,
        int height,
        float captureFps,
        BrowserMediaEngine enginePreference,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate)
    {
        this.input = input;
        this.width = width;
        this.height = height;
        this.captureFps = captureFps;
        this.enginePreference = enginePreference;
        this.autoplay = autoplay;
        currentLoop = loop;
        currentAudioEnabled = audioEnabled;
        currentVolume = volume;
        currentPlaybackRate = playbackRate;
    }

    public string Name => activeSource?.Name ?? $"YouTube browser ({DescribePreference(enginePreference)})";
    public int Width => activeSource?.Width ?? Math.Clamp(width, 320, 3840);
    public int Height => activeSource?.Height ?? Math.Clamp(height, 180, 2160);
    public float FramesPerSecond => activeSource?.FramesPerSecond ?? Math.Clamp(captureFps, 1.0f, 120.0f);
    public bool IsRunning => activeSource?.IsRunning ?? false;
    public string Status => string.IsNullOrWhiteSpace(fallbackStatus)
        ? activeSource?.Status ?? "browser source not started"
        : $"{activeSource?.Status ?? "browser source not started"}; {fallbackStatus}";

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
        if (activeSource != null)
            return activeSource.TryGetLatestFrame(out frame);

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

    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop)
    {
        currentAudioEnabled = audioEnabled;
        currentVolume = volume;
        currentPlaybackRate = playbackRate;
        currentLoop = loop;

        if (activeSource is IMediaPlaybackController controller)
            controller.ApplyPlaybackSettings(audioEnabled, volume, playbackRate, loop);
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

    public void Dispose()
    {
        activeSource?.Dispose();
        activeSource = null;
    }

    public float DetectedVideoFps
    {
        get
        {
            if (activeSource is IMediaPlaybackTelemetrySource telemetrySource && telemetrySource.TryGetPlaybackTelemetry(out var t))
                return t.DetectedVideoFps;

            return 0.0f;
        }
    }

    public void UpdateCaptureFps(float fps)
    {
        if (activeSource is CefYouTubeBrowserFrameSource cef)
            cef.UpdateCaptureFps(fps);
        else if (activeSource is WebView2YouTubeBrowserFrameSource webView2)
            webView2.UpdateCaptureFps(fps);
    }

    private void EnsureSource()
    {
        if (activeSource != null)
            return;

        try
        {
            activeSource = CreateSource();
        }
        catch (Exception ex)
        {
            activeSource = CreateFallbackAfterFailure(ex);
        }

        if (activeSource is IMediaPlaybackController controller)
            controller.ApplyPlaybackSettings(currentAudioEnabled, currentVolume, currentPlaybackRate, currentLoop);
    }

    private IVideoFrameSource CreateSource()
    {
        return enginePreference switch
        {
            BrowserMediaEngine.CefOffScreen => CreateCefSource(),
            BrowserMediaEngine.WebView2Capture => CreateWebView2Source(),
            _ => CreateAutoSource(),
        };
    }

    private IVideoFrameSource CreateAutoSource()
    {
        if (CefRuntimeManager.CanInitialize(out var status))
        {
            fallbackStatus = string.Empty;
            return CreateCefSource();
        }

        fallbackStatus = $"CEF unavailable, using WebView2 fallback: {status}";
        return CreateWebView2Source();
    }

    private IVideoFrameSource CreateCefSource()
    {
        if (!CefRuntimeManager.CanInitialize(out var cefStatus))
        {
            if (enginePreference == BrowserMediaEngine.Auto)
            {
                fallbackStatus = $"CEF unavailable, using WebView2 fallback: {cefStatus}";
                return CreateWebView2Source();
            }

            fallbackStatus = $"CEF unavailable: {cefStatus}";
            return CreateUnavailableSource(fallbackStatus);
        }

        fallbackStatus = enginePreference == BrowserMediaEngine.CefOffScreen ? string.Empty : fallbackStatus;
        activeEngine = BrowserMediaEngine.CefOffScreen;
        return CreateInitializedCefSource();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IVideoFrameSource CreateInitializedCefSource()
    {
        return new CefYouTubeBrowserFrameSource(input, width, height, captureFps, autoplay, currentLoop, currentAudioEnabled, currentVolume, currentPlaybackRate);
    }

    private IVideoFrameSource CreateWebView2Source()
    {
        activeEngine = BrowserMediaEngine.WebView2Capture;
        return new WebView2YouTubeBrowserFrameSource(input, width, height, captureFps, autoplay, currentLoop, currentAudioEnabled, currentVolume, currentPlaybackRate);
    }

    private void StartActiveSource()
    {
        if (activeSource == null)
            return;

        try
        {
            activeSource.Start();
        }
        catch (Exception ex)
        {
            activeSource = CreateFallbackAfterFailure(ex);

            if (activeSource is IMediaPlaybackController controller)
                controller.ApplyPlaybackSettings(currentAudioEnabled, currentVolume, currentPlaybackRate, currentLoop);

            try
            {
                activeSource.Start();
            }
            catch (Exception fallbackEx)
            {
                activeSource = CreateUnavailableSource(fallbackEx);
            }
        }
    }

    private IVideoFrameSource CreateFallbackAfterFailure(Exception ex)
    {
        var baseMessage = ex.GetBaseException().Message;
        Plugin.Log.Warning(ex, "Failed to start CrystalCast browser source.");

        if (activeEngine == BrowserMediaEngine.CefOffScreen && enginePreference == BrowserMediaEngine.Auto)
        {
            fallbackStatus = $"CEF failed, using WebView2 fallback: {baseMessage}";
            try
            {
                return CreateWebView2Source();
            }
            catch (Exception webViewEx)
            {
                Plugin.Log.Warning(webViewEx, "Failed to create CrystalCast WebView2 fallback source.");
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
        if (cefStatus.Contains("invalid YouTube", StringComparison.OrdinalIgnoreCase))
            return;

        activeSource.Dispose();
        activeSource = CreateWebView2Source();
        fallbackStatus = $"CEF failed, using WebView2 fallback: {cefStatus}";

        if (activeSource is IMediaPlaybackController controller)
            controller.ApplyPlaybackSettings(currentAudioEnabled, currentVolume, currentPlaybackRate, currentLoop);

        StartActiveSource();
    }

    private static string DescribePreference(BrowserMediaEngine engine)
    {
        return engine switch
        {
            BrowserMediaEngine.CefOffScreen => "CEF offscreen",
            BrowserMediaEngine.WebView2Capture => "WebView2 capture",
            _ => "auto",
        };
    }

    private sealed class UnavailableFrameSource(int width, int height, float framesPerSecond, string status) : IVideoFrameSource
    {
        public string Name => "YouTube browser (unavailable)";
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
