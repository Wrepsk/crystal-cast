using Dalamud.Plugin.Services;

namespace CrystalCast.Video;

/// <summary>
/// Experimental Wine-only provider player that renders in a native Linux WebKitGTK helper.
/// </summary>
internal sealed class WebKitGtkBrowserFrameSource : IVideoFrameSource, IMediaPlaybackTelemetrySource, IMediaPlaybackController, IBrowserFrameSourceRuntime, IBrowserControlsHost, IWebKitGtkHostListener
{
    private const int MaximumPendingPlayerMessages = 64;

    private readonly BrowserSourceDescriptor descriptor;
    private readonly string input;
    private readonly IBrowserSourceReference source;
    private readonly bool isValidSource;
    private readonly BrowserPlaybackIntent playbackIntent;
    private readonly BrowserPlayerPageResource? playerPage;
    private readonly BrowserPlayerState playerState;
    private readonly IPluginLog log;
    private readonly FrameCadenceDiagnostics cadenceDiagnostics = new();
    private readonly object playerMessageLock = new();
    private readonly Queue<string> pendingPlayerMessages = new();
    private WebKitGtkHostSession? session;
    private VideoFrame? latestFrame;
    private bool playerReady;
    private bool loop;
    private bool playlistAutoplayNext;
    private bool audioEnabled;
    private float volume;
    private float playbackRate;
    private volatile bool captureEnabled;
    private volatile bool controlsVisible;
    private volatile bool failed;
    private volatile string hostStatus = "stopped";
    private volatile string graphicsStatus = string.Empty;
    private long lastFrameUnixMs;
    private long captureWindowStartUnixMs;
    private int captureWindowFrames;
    private double measuredCaptureFps;
    private double lastExtractMilliseconds;
    private string logPath = string.Empty;
    private volatile bool disposed;

    public WebKitGtkBrowserFrameSource(
        BrowserSourceDescriptor descriptor,
        string input,
        int width,
        int height,
        float captureFps,
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
        isValidSource = descriptor.TryParse(input, out source);
        Width = Math.Clamp(width, 320, 3840);
        Height = Math.Clamp(height, 180, 2160);
        FramesPerSecond = Math.Clamp(captureFps, 1.0f, 120.0f);
        playbackIntent = new BrowserPlaybackIntent(autoplay);
        this.loop = loop;
        this.playlistAutoplayNext = playlistAutoplayNext;
        this.audioEnabled = audioEnabled;
        this.volume = BrowserPlayerState.QuantizeVolume(volume);
        this.playbackRate = BrowserPlayerState.ClampPlaybackRate(playbackRate);
        this.log = log;

        if (isValidSource)
        {
            playerPage = BrowserPlayerPageResource.Create(
                descriptor,
                source,
                new BrowserPlaybackSettings(autoplay, loop, playlistAutoplayNext, audioEnabled, this.volume, this.playbackRate));
        }
        else
        {
            hostStatus = descriptor.InvalidSourceMessage;
        }

        playerState = new BrowserPlayerState(descriptor, source, this.playbackRate);
    }

    public BrowserSourceProviderKind ProviderKind => descriptor.ProviderKind;
    public string Name => $"{descriptor.DisplayName} browser (native WebKitGTK, experimental)";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; private set; }
    public bool IsRunning => captureEnabled && !failed;
    public float DetectedVideoFps => playerState.DetectedVideoFps;
    public bool BrowserControlsAvailable => isValidSource && !failed;
    public bool BrowserControlsVisible => controlsVisible;

    public string Status
    {
        get
        {
            var frameAge = lastFrameUnixMs == 0
                ? "no captured frame"
                : $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs} ms frame age";
            var runtime = session?.RuntimeDescription ?? "not started";
            var graphics = graphicsStatus.Length == 0 ? string.Empty : $"; {graphicsStatus}";
            return $"capture {measuredCaptureFps:0.#}/{FramesPerSecond:0.#} fps; {cadenceDiagnostics.Status}; {lastExtractMilliseconds:0.#} ms extract; {frameAge}; {hostStatus}; {runtime}{graphics}; {playerState.PlayerStatus}";
        }
    }

    public void Start()
    {
        var wasCaptureEnabled = captureEnabled;
        if (!EnsureSession())
            return;

        captureEnabled = true;
        session?.SetCaptureEnabled(true);
        if (!wasCaptureEnabled && playbackIntent.IsPlayRequested)
            PostPlayerMessage(BrowserPageMessaging.Play(GetNonce()));
    }

    public void Stop()
    {
        var wasCaptureEnabled = captureEnabled;
        captureEnabled = false;
        session?.SetCaptureEnabled(false);
        if (wasCaptureEnabled)
            PostPlayerMessage(BrowserPageMessaging.Pause(GetNonce()));

        playerState.SetState(ScreenPlaybackState.Paused, playbackRate);
    }

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        var current = Volatile.Read(ref latestFrame);
        if (current != null)
            return current.TryAcquire(out frame);

        frame = null!;
        return false;
    }

    public bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry telemetry)
    {
        telemetry = playerState.Telemetry;
        return isValidSource;
    }

    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        volume = BrowserPlayerState.QuantizeVolume(volume);
        playbackRate = BrowserPlayerState.ClampPlaybackRate(playbackRate);
        if (this.audioEnabled == audioEnabled
            && Math.Abs(this.volume - volume) < 0.001f
            && Math.Abs(this.playbackRate - playbackRate) < 0.001f
            && this.loop == loop
            && this.playlistAutoplayNext == playlistAutoplayNext)
        {
            return;
        }

        this.audioEnabled = audioEnabled;
        this.volume = volume;
        this.playbackRate = playbackRate;
        this.loop = loop;
        this.playlistAutoplayNext = playlistAutoplayNext;
        session?.SetMuted(IsMuted(audioEnabled, volume));
        PostPlayerMessage(BrowserPageMessaging.Settings(GetNonce(), audioEnabled, volume, playbackRate, loop, playlistAutoplayNext));
    }

    public void Play()
    {
        playbackIntent.RequestPlay();
        if (!EnsureSession())
            return;

        captureEnabled = true;
        session?.SetCaptureEnabled(true);
        PostPlayerMessage(BrowserPageMessaging.Play(GetNonce()));
    }

    public void Pause()
    {
        playbackIntent.RequestPause();
        captureEnabled = false;
        session?.SetCaptureEnabled(false);
        PostPlayerMessage(BrowserPageMessaging.Pause(GetNonce()));
        playerState.SetState(ScreenPlaybackState.Paused, playbackRate);
    }

    public void SeekBy(double seconds) => PostPlayerMessage(BrowserPageMessaging.SeekBy(GetNonce(), seconds));

    public void SeekTo(double seconds) => PostPlayerMessage(BrowserPageMessaging.SeekTo(GetNonce(), seconds));

    public void Restart()
    {
        playbackIntent.RequestPlay();
        if (!EnsureSession())
            return;

        captureEnabled = true;
        session?.SetCaptureEnabled(true);
        PostPlayerMessage(BrowserPageMessaging.Restart(GetNonce()));
    }

    public void UpdateCaptureFps(float fps)
    {
        var clamped = Math.Clamp(fps, 1.0f, 120.0f);
        if (Math.Abs(FramesPerSecond - clamped) < 0.01f)
            return;

        FramesPerSecond = clamped;
        session?.SetFramesPerSecond(clamped);
    }

    public bool ShowBrowserControls()
    {
        if (!EnsureSession())
            return false;

        controlsVisible = true;
        session?.SetControlsVisible(true);
        hostStatus = "browser controls are in the CrystalCast browser window on your Linux desktop";
        return true;
    }

    public bool HideBrowserControls()
    {
        if (session == null)
            return false;

        controlsVisible = false;
        session.SetControlsVisible(false);
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        captureEnabled = false;
        session?.Dispose();
        session = null;
        Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        hostStatus = "disposed";
        playerState.SetState(ScreenPlaybackState.Stopped, playbackRate);
        if (!failed)
            TryDeleteLog();
    }

    void IWebKitGtkHostListener.OnHostStatus(string status) => hostStatus = status;

    void IWebKitGtkHostListener.OnHostFailed(string reason)
    {
        failed = true;
        captureEnabled = false;
        hostStatus = $"native WebKitGTK browser unavailable: {reason}";
        log.Warning("CrystalCast native WebKitGTK browser failed: {Reason}", reason);
    }

    void IWebKitGtkHostListener.OnHostGraphics(string browserTexture, string outputTexture) =>
        graphicsStatus = $"browser texture {browserTexture}, captured as {outputTexture}";

    void IWebKitGtkHostListener.OnHostFrame(VideoFrame frame, double extractMilliseconds)
    {
        if (disposed || !captureEnabled)
        {
            frame.Dispose();
            return;
        }

        lastExtractMilliseconds = extractMilliseconds;
        Interlocked.Exchange(ref latestFrame, frame)?.Dispose();
        if (disposed && Interlocked.CompareExchange(ref latestFrame, null, frame) == frame)
        {
            frame.Dispose();
            return;
        }

        RecordCapturedFrame(frame.TimestampUnixMs);
    }

    void IWebKitGtkHostListener.OnPageLoading()
    {
        lock (playerMessageLock)
        {
            playerReady = false;
        }
    }

    void IWebKitGtkHostListener.OnPageMessage(string json)
    {
        var page = playerPage;
        if (page == null)
            return;

        if (!BrowserMessageValidator.TryParseAuthenticated(json, page.Nonce, out var document, out var error))
        {
            playerState.PlayerStatus = $"invalid browser message: {error}";
            return;
        }

        using (document!)
        {
            if (playerState.HandleMessage(document!.RootElement, page.Nonce, playbackRate) != BrowserPlayerMessageResult.Ready)
                return;
        }

        lock (playerMessageLock)
        {
            playerReady = true;
            while (pendingPlayerMessages.TryDequeue(out var message))
                session?.PostToPage(message);
        }

        if (playbackIntent.IsPlayRequested && captureEnabled)
            PostPlayerMessage(BrowserPageMessaging.Play(page.Nonce));
    }

    void IWebKitGtkHostListener.OnControlsVisibilityChanged(bool visible) => controlsVisible = visible;

    private bool EnsureSession()
    {
        if (!isValidSource || playerPage == null)
        {
            hostStatus = descriptor.ParseInvalidInputStatus(input);
            return false;
        }

        if (failed)
            return false;

        if (session != null)
            return true;

        if (!WebKitGtkAvailability.TryGetHelper(out var helper, out var launcher, out var unavailable))
        {
            failed = true;
            hostStatus = $"native WebKitGTK browser unavailable: {unavailable}";
            return false;
        }

        logPath = WebKitGtkAvailability.CreateLogPath();
        session = new WebKitGtkHostSession(
            new WebKitGtkHostOptions(helper, launcher, logPath, Width, Height, FramesPerSecond),
            this);
        session.SetMuted(IsMuted(audioEnabled, volume));
        session.LoadPage(playerPage.Url, playerPage.Html);
        session.Start();
        hostStatus = "starting native WebKitGTK browser";
        return true;
    }

    private void PostPlayerMessage(string json)
    {
        lock (playerMessageLock)
        {
            if (!playerReady || session == null)
            {
                if (pendingPlayerMessages.Count >= MaximumPendingPlayerMessages)
                    pendingPlayerMessages.Dequeue();
                pendingPlayerMessages.Enqueue(json);
                return;
            }

            session.PostToPage(json);
        }
    }

    private string GetNonce() => playerPage?.Nonce ?? string.Empty;

    private static bool IsMuted(bool audioEnabled, float volume) => !audioEnabled || volume <= 0.001f;

    private void RecordCapturedFrame(long now)
    {
        if (captureWindowStartUnixMs == 0)
            captureWindowStartUnixMs = now;

        lastFrameUnixMs = now;
        cadenceDiagnostics.Record(FramesPerSecond);
        captureWindowFrames++;
        var elapsedMs = now - captureWindowStartUnixMs;
        if (elapsedMs >= 1000)
        {
            measuredCaptureFps = captureWindowFrames * 1000.0 / elapsedMs;
            captureWindowFrames = 0;
            captureWindowStartUnixMs = now;
        }
    }

    private void TryDeleteLog()
    {
        try
        {
            if (logPath.Length > 0)
                File.Delete(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class WebKitGtkAvailability
{
    public const string HelperFileName = "crystalcast-webkit-host";
    public const string LauncherFileName = "crystalcast-webkit-host.sh";

    public static string NativeDirectory
    {
        get
        {
            // Dalamud reports the installed plugin folder; the assembly location is a fallback.
            var directory = Plugin.PluginInterface?.AssemblyLocation?.DirectoryName;
            if (string.IsNullOrEmpty(directory))
                directory = Path.GetDirectoryName(typeof(WebKitGtkAvailability).Assembly.Location);
            return Path.Combine(string.IsNullOrEmpty(directory) ? AppContext.BaseDirectory : directory, "native", "linux-x64");
        }
    }

    public static bool TryGetHelper(out string helper, out string launcher, out string reason)
    {
        helper = Path.Combine(NativeDirectory, HelperFileName);
        launcher = Path.Combine(NativeDirectory, LauncherFileName);
        if (!WineEnvironment.IsWine)
        {
            reason = "it only runs under Wine on Linux";
            return false;
        }

        if (!WineHostInterop.IsLinuxHost)
        {
            reason = $"it needs a Linux host; Wine reports {WineHostInterop.HostSystemName}";
            return false;
        }

        if (!File.Exists(helper) || !File.Exists(launcher))
        {
            reason = "this CrystalCast build does not include the native Linux helper";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static string Describe()
    {
        return TryGetHelper(out _, out _, out var reason)
            ? "native helper included; needs WebKitGTK 6.0 (GTK 4) installed on Linux"
            : reason;
    }

    public static string CreateLogPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrystalCast");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"webkitgtk-host-{Guid.NewGuid():N}.log");
    }
}
