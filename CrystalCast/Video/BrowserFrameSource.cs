namespace CrystalCast.Video;

using Dalamud.Plugin.Services;

internal sealed class BrowserFrameSource : IVideoFrameSource, INativeVideoFrameSource, INativeVideoFrameAcknowledgement, IMediaPlaybackTelemetrySource, IMediaPlaybackController, IBrowserFrameSourceRuntime, IBrowserControlsHost
{
    private readonly BrowserSourceDescriptor descriptor;
    private readonly string input;
    private readonly int width;
    private readonly int height;
    private readonly float captureFps;
    private readonly BrowserMediaEngine enginePreference;
    private readonly bool autoplay;
    private readonly IPluginLog log;
    private bool currentLoop;
    private bool currentPlaylistAutoplayNext;
    private bool currentAudioEnabled;
    private float currentVolume;
    private float currentPlaybackRate;
    private IVideoFrameSource? activeSource;

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
        float playbackRate,
        IPluginLog log)
    {
        this.descriptor = descriptor;
        this.input = input;
        this.width = width;
        this.height = height;
        this.captureFps = captureFps;
        this.enginePreference = BrowserMediaEnginePolicy.Normalize(enginePreference);
        this.autoplay = autoplay;
        this.log = log;
        currentLoop = loop;
        currentPlaylistAutoplayNext = playlistAutoplayNext;
        currentAudioEnabled = audioEnabled;
        currentVolume = volume;
        currentPlaybackRate = playbackRate;
    }

    public BrowserSourceProviderKind ProviderKind => descriptor.ProviderKind;
    public string Name => activeSource?.Name ?? $"{descriptor.DisplayName} browser ({DescribeCaptureMode()})";
    public int Width => activeSource?.Width ?? Math.Clamp(width, 320, 3840);
    public int Height => activeSource?.Height ?? Math.Clamp(height, 180, 2160);
    public float FramesPerSecond => activeSource?.FramesPerSecond ?? Math.Clamp(captureFps, 1.0f, 120.0f);
    public bool IsRunning => activeSource?.IsRunning ?? false;
    public string Status => activeSource?.Status ?? "browser source not started";
    public bool BrowserControlsAvailable => activeSource is IBrowserControlsHost { BrowserControlsAvailable: true }
        || activeSource == null;
    public bool BrowserControlsVisible => activeSource is IBrowserControlsHost { BrowserControlsVisible: true };

    public float DetectedVideoFps
    {
        get
        {
            if (activeSource is IMediaPlaybackTelemetrySource telemetrySource
                && telemetrySource.TryGetPlaybackTelemetry(out var telemetry))
            {
                return telemetry.DetectedVideoFps;
            }

            return 0.0f;
        }
    }

    public void Start()
    {
        EnsureSource();
        var candidate = activeSource;
        if (candidate == null)
            return;

        activeSource = null;
        activeSource = BrowserCandidateLifetime.TryUse(candidate, candidate.Start, out var error, log)
            ? candidate
            : CreateUnavailableSource(error!);
    }

    public void Stop() => activeSource?.Stop();

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        if (activeSource != null)
            return activeSource.TryGetLatestFrame(out frame);

        frame = null!;
        return false;
    }

    public bool TryGetLatestNativeFrame(out NativeVideoFrame frame)
    {
        if (activeSource is INativeVideoFrameSource nativeSource)
            return nativeSource.TryGetLatestNativeFrame(out frame);

        frame = null!;
        return false;
    }

    public void AcknowledgeNativeFrame(IntPtr sharedHandle)
    {
        if (activeSource is INativeVideoFrameAcknowledgement acknowledgement)
            acknowledgement.AcknowledgeNativeFrame(sharedHandle);
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
            log.Warning(ex, "Failed to create CrystalCast WebView2 browser source.");
            activeSource = CreateUnavailableSource(ex);
            return;
        }

        activeSource = BrowserCandidateLifetime.TryUse(candidate, () => ApplyCurrentSettings(candidate), out var error, log)
            ? candidate
            : CreateUnavailableSource(error!);
    }

    private IVideoFrameSource CreateSource()
    {
        var captureMode = BrowserPlatformPolicy.ResolveCaptureMode(enginePreference, WineEnvironment.IsWine);
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
            captureMode,
            log);
    }

    private void ApplyCurrentSettings(IVideoFrameSource candidate)
    {
        if (candidate is IMediaPlaybackController controller)
        {
            controller.ApplyPlaybackSettings(
                currentAudioEnabled,
                currentVolume,
                currentPlaybackRate,
                currentLoop,
                currentPlaylistAutoplayNext);
        }
    }

    private string DescribeCaptureMode()
    {
        return BrowserPlatformPolicy.ResolveCaptureMode(enginePreference, WineEnvironment.IsWine) == WebView2CaptureMode.PreviewJpeg
            ? WineEnvironment.IsWine ? "WebView2 JPEG capture, Wine compatibility" : "WebView2 JPEG capture"
            : "WebView2 window capture";
    }

    private IVideoFrameSource CreateUnavailableSource(Exception ex)
    {
        return new UnavailableFrameSource(
            Width,
            Height,
            FramesPerSecond,
            $"WebView2 browser source unavailable: {ex.GetBaseException().Message}");
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
