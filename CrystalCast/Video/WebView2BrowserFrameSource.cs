using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Dalamud.Plugin.Services;

namespace CrystalCast.Video;

internal sealed class WebView2BrowserFrameSource : IVideoFrameSource, INativeVideoFrameSource, INativeVideoFrameAcknowledgement, IMediaPlaybackTelemetrySource, IMediaPlaybackController, IBrowserFrameSourceRuntime, ICaptureRateSource, IBrowserControlsHost
{
    private readonly BrowserSourceDescriptor descriptor;
    private readonly string input;
    private readonly IBrowserSourceReference source;
    private readonly bool isValidSource;
    private readonly BrowserPlaybackIntent playbackIntent;
    private readonly BrowserPlayerPageResource? playerPage;
    private readonly WebView2CaptureMode captureMode;
    private readonly JpegCaptureResolution jpegResolution;
    private readonly IPluginLog log;
    private readonly FrameCadenceDiagnostics cadenceDiagnostics = new();
    private readonly BrowserPlayerState playerState;
    private BrowserThread? browserThread;
    private VideoFrame? latestFrame;
    private NativeVideoFrame? latestNativeFrame;
    private bool loop;
    private bool playlistAutoplayNext;
    private bool audioEnabled;
    private float volume;
    private float playbackRate;
    private volatile bool captureEnabled;
    private volatile string browserStatus = "stopped";
    private volatile string nativeCaptureFailure = string.Empty;
    private string? browserExecutableFolder;
    private long sequence;
    private long lastFrameUnixMs;
    private long captureWindowStartUnixMs;
    private int captureWindowFrames;
    private double measuredCaptureFps;
    private double lastCaptureMilliseconds;
    private volatile bool disposed;

    public WebView2BrowserFrameSource(
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
        WebView2CaptureMode captureMode,
        IPluginLog log,
        JpegCaptureResolution jpegResolution = JpegCaptureResolution.Full1080p)
    {
        this.jpegResolution = jpegResolution;
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
        this.captureMode = captureMode;
        this.log = log;

        if (isValidSource)
        {
            playerPage = BrowserPlayerPageResource.Create(
                descriptor,
                source,
                new BrowserPlaybackSettings(autoplay, loop, playlistAutoplayNext, audioEnabled, this.volume, this.playbackRate));
        }

        if (!isValidSource)
            browserStatus = descriptor.InvalidSourceMessage;

        playerState = new BrowserPlayerState(descriptor, source, this.playbackRate);
    }

    public BrowserSourceProviderKind ProviderKind => descriptor.ProviderKind;
    public string Name => captureMode == WebView2CaptureMode.WindowGraphicsCapture
        ? $"{descriptor.DisplayName} browser (WebView2 window capture)"
        : $"{descriptor.DisplayName} browser (WebView2 JPEG capture)";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; private set; }
    public bool IsRunning => captureEnabled;
    public float DetectedVideoFps => playerState.DetectedVideoFps;
    public float MeasuredCaptureFps => (float)Volatile.Read(ref measuredCaptureFps);
    public bool BrowserControlsAvailable => isValidSource;
    public bool BrowserControlsVisible => browserThread?.BrowserControlsVisible == true;

    public string Status
    {
        get
        {
            var frameAge = lastFrameUnixMs == 0
                ? "no captured frame"
                : $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs} ms frame age";
            var failureStatus = string.IsNullOrWhiteSpace(nativeCaptureFailure)
                ? string.Empty
                : $"; WGC failure {nativeCaptureFailure}";
            return $"capture {measuredCaptureFps:0.#}/{FramesPerSecond:0.#} fps; {cadenceDiagnostics.Status}; {lastCaptureMilliseconds:0.#} ms; {frameAge}; {browserStatus}{failureStatus}; {playerState.PlayerStatus}";
        }
    }

    public void Start()
    {
        var wasCaptureEnabled = captureEnabled;
        if (!EnsureBrowserThread())
            return;

        captureEnabled = true;
        if (!wasCaptureEnabled && playbackIntent.IsPlayRequested)
            browserThread?.Play();
    }

    public void Stop()
    {
        var wasCaptureEnabled = captureEnabled;
        captureEnabled = false;
        if (wasCaptureEnabled)
            browserThread?.Pause();

        playerState.SetState(ScreenPlaybackState.Paused, playbackRate);
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
        browserThread?.ApplyPlaybackSettings(audioEnabled, volume, playbackRate, loop, playlistAutoplayNext);
    }

    public void Play()
    {
        playbackIntent.RequestPlay();
        EnsureBrowserThread();
        captureEnabled = true;
        browserThread?.Play();
    }

    public void Pause()
    {
        playbackIntent.RequestPause();
        captureEnabled = false;
        browserThread?.Pause();
        playerState.SetState(ScreenPlaybackState.Paused, playbackRate);
    }

    public void SeekBy(double seconds)
    {
        browserThread?.SeekBy(seconds);
    }

    public void SeekTo(double seconds)
    {
        browserThread?.SeekTo(seconds);
    }

    public void Restart()
    {
        playbackIntent.RequestPlay();
        EnsureBrowserThread();
        captureEnabled = true;
        browserThread?.Restart();
    }

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        var current = Volatile.Read(ref latestFrame);
        if (current != null)
            return current.TryAcquire(out frame);

        frame = null!;
        return false;
    }

    public bool TryGetLatestNativeFrame(out NativeVideoFrame frame)
    {
        frame = latestNativeFrame!;
        return frame != null;
    }

    public void AcknowledgeNativeFrame(IntPtr sharedHandle) =>
        browserThread?.AcknowledgeNativeFrame(sharedHandle);

    public bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry currentTelemetry)
    {
        currentTelemetry = playerState.Telemetry;
        return isValidSource;
    }

    public void Dispose()
    {
        disposed = true;
        captureEnabled = false;
        Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        browserThread?.Dispose();
        browserThread = null;
        browserStatus = "disposed";
        playerState.SetState(ScreenPlaybackState.Stopped, playbackRate);
    }

    public void UpdateCaptureFps(float fps)
    {
        var clamped = Math.Clamp(fps, 1.0f, 120.0f);
        if (Math.Abs(FramesPerSecond - clamped) < 0.01f)
            return;

        FramesPerSecond = clamped;
    }

    public bool ShowBrowserControls()
    {
        if (!EnsureBrowserThread())
            return false;

        return browserThread?.ShowBrowserControls() == true;
    }

    public bool HideBrowserControls()
    {
        if (browserThread == null)
            return false;

        browserThread.HideBrowserControls();
        return true;
    }

    private bool EnsureBrowserThread()
    {
        if (!isValidSource)
        {
            browserStatus = descriptor.ParseInvalidInputStatus(input);
            return false;
        }

        if (browserThread != null)
            return true;

        // A disposed source must never start a new browser: nothing would be left to close it.
        if (disposed)
            return false;

        if (!TryGetWebView2Runtime(out var runtimeVersion, out var runtimeError, out var browserFolder))
        {
            browserStatus = runtimeError;
            return false;
        }

        browserExecutableFolder = browserFolder;
        browserStatus = $"starting WebView2 {runtimeVersion}";
        browserThread = new BrowserThread(this);
        return true;
    }

    private void PublishFrame(VideoFrame frame)
    {
        if (disposed)
        {
            frame.Dispose();
            return;
        }

        Interlocked.Exchange(ref latestFrame, frame)?.Dispose();
        if (disposed && Interlocked.CompareExchange(ref latestFrame, null, frame) == frame)
        {
            frame.Dispose();
            return;
        }

        RecordCapturedFrame(frame.TimestampUnixMs);
    }

    private void PublishNativeFrame(IntPtr sharedHandle, int width, int height)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = new NativeVideoFrame(sharedHandle, width, height, Interlocked.Increment(ref sequence), now);
        Interlocked.Exchange(ref latestNativeFrame, frame);
        RecordCapturedFrame(now);
    }

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

    private void UpdateFromWebMessage(JsonElement root)
    {
        if (playerPage == null
            || playerState.HandleMessage(root, playerPage.Nonce, playbackRate) != BrowserPlayerMessageResult.Ready)
            return;

        browserThread?.MarkPlayerReady();
        browserThread?.StartWindowCaptureWhenReady();
        if (playbackIntent.IsPlayRequested && captureEnabled)
            browserThread?.Play();
    }

    internal static bool TryGetWebView2Runtime(out string runtimeVersion, out string error)
    {
        return TryGetWebView2Runtime(out runtimeVersion, out error, out _);
    }

    /// <summary>
    /// Also returns the downloaded runtime's folder, or null for the installed runtime, to pass to
    /// CoreWebView2Environment.CreateAsync.
    /// </summary>
    internal static bool TryGetWebView2Runtime(out string runtimeVersion, out string error, out string? browserExecutableFolder)
    {
        var found = WebView2FixedRuntime.TryFind(out var runtime, out error);
        runtimeVersion = found ? runtime.Describe() : string.Empty;
        browserExecutableFolder = found ? runtime.BrowserExecutableFolder : null;
        return found;
    }

    private sealed class BrowserThread : IDisposable
    {
        private readonly WebView2BrowserFrameSource owner;
        private readonly Thread thread;
        private readonly BrowserLifecycle lifecycle = new();
        private readonly CancellationTokenSource shutdownCancellation = new();
        private readonly ConcurrentQueue<Func<Task>> pendingActions = new();
        private readonly Queue<string> pendingPlayerMessages = new();
        private readonly WebView2JpegFrameCapture jpegCapture = new();
        private readonly WebView2ScreencastCapture screencast;
        private readonly WebView2ScreencastSettings screencastSettings;
        private bool screencastOutcomeLogged;
        private volatile bool shutdownRequested;
        private BrowserSynchronizationContext? synchronizationContext;
        private CoreWebView2Environment? environment;
        private CoreWebView2Controller? controller;
        private CoreWebView2? webView;
        private WebView2HostWindow? hostWindow;
        private WebView2WindowCaptureSession? windowCaptureSession;
        private Task? captureLoopTask;
        private bool windowCaptureStartRequested;
        private bool playerReady;
        private volatile bool browserControlsVisible;
        private long interactionDismissedAtTick;

        public BrowserThread(WebView2BrowserFrameSource owner)
        {
            this.owner = owner;
            screencastSettings = WebView2ScreencastPolicy.Resolve(
                owner.Width,
                owner.Height,
                owner.jpegResolution,
                Environment.GetEnvironmentVariable(WebView2ScreencastPolicy.OverrideVariable));
            screencast = new WebView2ScreencastCapture(
                owner.PublishFrame,
                () => Interlocked.Increment(ref owner.sequence),
                () => owner.FramesPerSecond);
            lifecycle.TryStart();
            thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = $"CrystalCast WebView2 {owner.descriptor.DisplayName} source",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public bool BrowserControlsVisible => browserControlsVisible;

        public void Play()
        {
            PostPlayerMessage(BrowserPageMessaging.Play(GetMessageNonce()));
        }

        public void Pause()
        {
            PostPlayerMessage(BrowserPageMessaging.Pause(GetMessageNonce()));
        }

        public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
        {
            var message = BrowserPageMessaging.Settings(GetMessageNonce(), audioEnabled, volume, playbackRate, loop, playlistAutoplayNext);
            Post(() =>
            {
                ApplyBrowserMute(audioEnabled, volume);
                SendOrQueuePlayerMessage(message);
                return Task.CompletedTask;
            });
        }

        public void SeekBy(double seconds)
        {
            PostPlayerMessage(BrowserPageMessaging.SeekBy(GetMessageNonce(), seconds));
        }

        public void SeekTo(double seconds)
        {
            PostPlayerMessage(BrowserPageMessaging.SeekTo(GetMessageNonce(), seconds));
        }

        public void Restart()
        {
            PostPlayerMessage(BrowserPageMessaging.Restart(GetMessageNonce()));
        }

        public bool ShowBrowserControls()
        {
            if (!WebView2HostWindow.CanReopenInteraction(
                    Volatile.Read(ref interactionDismissedAtTick),
                    Environment.TickCount64))
            {
                return false;
            }

            browserControlsVisible = true;
            Post(() =>
            {
                if (hostWindow != null && controller != null)
                {
                    var (width, height) = hostWindow.GetInteractionClientSize();
                    controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
                    hostWindow.ShowForInteraction(width, height);
                }

                owner.browserStatus = "WebView2 browser controls visible";
                return Task.CompletedTask;
            });
            return true;
        }

        public void HideBrowserControls()
        {
            browserControlsVisible = false;
            Post(() =>
            {
                ReturnToCaptureMode();
                return Task.CompletedTask;
            });
        }

        private void OnInteractionDismissed()
        {
            if (!browserControlsVisible)
                return;

            Volatile.Write(ref interactionDismissedAtTick, Environment.TickCount64);
            browserControlsVisible = false;
            ReturnToCaptureMode();
        }

        private void ReturnToCaptureMode()
        {
            if (hostWindow != null && controller != null)
            {
                controller.Bounds = new System.Drawing.Rectangle(0, 0, owner.Width, owner.Height);
                hostWindow.ReturnToCapture();
            }

            owner.browserStatus = owner.captureMode == WebView2CaptureMode.WindowGraphicsCapture
                ? "WebView2 window capture controls hidden"
                : "WebView2 JPEG capture controls hidden";
        }

        public void Dispose()
        {
            if (!lifecycle.TryBeginStopping())
                return;

            shutdownRequested = true;
            shutdownCancellation.Cancel();
            synchronizationContext?.Post(_ => { }, null);
        }

        private void Post(Func<Task> action)
        {
            if (!lifecycle.CanAcceptCommands)
                return;

            pendingActions.Enqueue(action);
            synchronizationContext?.Post(_ => DrainPostedActions(), null);
        }

        private void PostPlayerMessage(string json)
        {
            Post(() =>
            {
                SendOrQueuePlayerMessage(json);
                return Task.CompletedTask;
            });
        }

        private string GetMessageNonce()
        {
            return owner.playerPage?.Nonce ?? string.Empty;
        }

        private void SendOrQueuePlayerMessage(string json)
        {
            if (webView == null || !playerReady)
            {
                if (pendingPlayerMessages.Count >= 64)
                    pendingPlayerMessages.Dequeue();
                pendingPlayerMessages.Enqueue(json);
                return;
            }

            webView.PostWebMessageAsJson(json);
        }

        public void MarkPlayerReady()
        {
            Post(() =>
            {
                playerReady = true;
                while (webView != null && pendingPlayerMessages.TryDequeue(out var message))
                    webView.PostWebMessageAsJson(message);
                return Task.CompletedTask;
            });
        }

        private void DrainPostedActions()
        {
            while (lifecycle.CanAcceptCommands && pendingActions.TryDequeue(out var action))
                _ = RunPostedActionAsync(action);
        }

        private async Task RunPostedActionAsync(Func<Task> action)
        {
            if (!lifecycle.CanAcceptCommands)
                return;

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                owner.browserStatus = $"WebView2 command failed: {ex.Message}";
            }
        }

        private void ThreadMain()
        {
            var context = new BrowserSynchronizationContext();
            synchronizationContext = context;
            SynchronizationContext.SetSynchronizationContext(context);

            Task? initializationTask = null;
            try
            {
                if (lifecycle.CanAcceptCommands)
                    initializationTask = InitializeAsync(shutdownCancellation.Token);
                DrainPostedActions();
                // Only ever set the flag here: writing a copy back would erase a shutdown that Dispose
                // requested while the messages were being pumped, and the browser would keep playing forever.
                while (!shutdownRequested && !shutdownCancellation.IsCancellationRequested)
                {
                    var quitMessage = false;
                    BrowserNativeMessagePump.PumpMessages(ref quitMessage);
                    if (quitMessage)
                        shutdownRequested = true;
                    context.ExecutePending();
                    hostWindow?.PollInteractionFocus();
                    BrowserNativeMessagePump.WaitForWork(context.WorkAvailable, 50);
                }

                // A browser that is still starting gets more time: once it arrives, the startup code sees the
                // shutdown and the cleanup below closes it. Leaving earlier would strand it with nobody to close it.
                var cleanupStart = Environment.TickCount64;
                while ((initializationTask is { IsCompleted: false } || captureLoopTask is { IsCompleted: false })
                    && Environment.TickCount64 - cleanupStart < (initializationTask is { IsCompleted: false } ? 15000 : 3000))
                {
                    var shouldQuit = false;
                    BrowserNativeMessagePump.PumpMessages(ref shouldQuit);
                    context.ExecutePending();
                    BrowserNativeMessagePump.WaitForWork(context.WorkAvailable, 25);
                }
            }
            catch (Exception ex)
            {
                owner.browserStatus = $"WebView2 thread failed: {ex.Message}";
            }
            finally
            {
                // Each step on its own, closing the browser first: a failure in one step must never leave a
                // browser running with nobody to close it.
                WebView2BrowserGuards.BestEffort(() =>
                {
                    if (webView != null)
                    {
                        webView.WebResourceRequested -= OnWebResourceRequested;
                        webView.WebMessageReceived -= OnWebMessageReceived;
                        webView.NavigationStarting -= OnNavigationStarting;
                        webView.NavigationCompleted -= OnNavigationCompleted;
                        webView.ProcessFailed -= OnProcessFailed;
                        webView.NewWindowRequested -= WebView2BrowserGuards.OnNewWindowRequested;
                        webView.PermissionRequested -= WebView2BrowserGuards.OnPermissionRequested;
                        webView.DownloadStarting -= WebView2BrowserGuards.OnDownloadStarting;
                    }
                });
                webView = null;
                WebView2BrowserGuards.BestEffort(() => controller?.Close());
                controller = null;
                WebView2BrowserGuards.BestEffort(DisposeWindowCaptureSession);
                WebView2BrowserGuards.BestEffort(() =>
                {
                    if (hostWindow != null)
                        hostWindow.InteractionDismissed -= OnInteractionDismissed;
                    hostWindow?.Dispose();
                });
                hostWindow = null;

                environment = null;
                lifecycle.MarkStopped();
                screencast.Dispose();
                jpegCapture.Dispose();
                shutdownCancellation.Dispose();
                context.Dispose();
            }
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var userDataFolder = BrowserProfileManager.GetWebView2UserDataFolder(owner.descriptor.ProviderKind);
                Directory.CreateDirectory(userDataFolder);
                cancellationToken.ThrowIfCancellationRequested();

                hostWindow = WebView2HostWindow.Create(owner.Width, owner.Height);
                hostWindow.InteractionDismissed += OnInteractionDismissed;
                var parentHwnd = hostWindow.Hwnd;

                var environmentOptions = new CoreWebView2EnvironmentOptions(
                    additionalBrowserArguments: WebView2BrowserArguments.Combine(owner.descriptor.WebView2AdditionalBrowserArguments));
                environment = await CoreWebView2Environment.CreateAsync(owner.browserExecutableFolder, userDataFolder, environmentOptions);
                WebView2BrowserGuards.TrackBrowserProcesses(environment);
                cancellationToken.ThrowIfCancellationRequested();

                controller = await environment.CreateCoreWebView2ControllerAsync(parentHwnd);
                cancellationToken.ThrowIfCancellationRequested();
                controller.Bounds = new System.Drawing.Rectangle(0, 0, owner.Width, owner.Height);
                controller.IsVisible = true;
                controller.DefaultBackgroundColor = System.Drawing.Color.Black;
                hostWindow.ShowForCapture();

                webView = controller.CoreWebView2;
                WebView2ProcessTracker.Track((int)webView.BrowserProcessId);
                WebView2BrowserGuards.ConfigureWebView(webView);
                ApplyBrowserMute(owner.audioEnabled, owner.volume);
                webView.WebMessageReceived += OnWebMessageReceived;
                webView.NavigationStarting += OnNavigationStarting;
                webView.NavigationCompleted += OnNavigationCompleted;
                webView.ProcessFailed += OnProcessFailed;
                webView.NewWindowRequested += WebView2BrowserGuards.OnNewWindowRequested;
                webView.PermissionRequested += WebView2BrowserGuards.OnPermissionRequested;
                webView.DownloadStarting += WebView2BrowserGuards.OnDownloadStarting;
                webView.WebResourceRequested += OnWebResourceRequested;
                var page = owner.playerPage ?? throw new InvalidOperationException("Player page was not created.");
                webView.AddWebResourceRequestedFilter(page.Url, CoreWebView2WebResourceContext.Document);
                playerReady = false;
                owner.browserStatus = $"WebView2 loading {owner.descriptor.DisplayName} player";
                webView.Navigate(page.Url);

                if (!lifecycle.TryMarkRunning())
                    cancellationToken.ThrowIfCancellationRequested();
                EnsureCaptureLoopStarted();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                lifecycle.MarkFaulted();
                owner.browserStatus = $"WebView2 init failed: {ex.Message}";
            }
        }

        public void StartWindowCaptureWhenReady()
        {
            if (owner.captureMode != WebView2CaptureMode.WindowGraphicsCapture
                || !lifecycle.CanAcceptCommands
                || windowCaptureSession != null
                || windowCaptureStartRequested)
                return;

            windowCaptureStartRequested = true;
            Post(() =>
            {
                StartWindowCaptureOrFallback();
                return Task.CompletedTask;
            });
        }

        private void StartWindowCaptureOrFallback()
        {
            owner.nativeCaptureFailure = string.Empty;
            try
            {
                if (TryStartWindowCapture())
                    return;
            }
            catch (Exception ex)
            {
                DisposeWindowCaptureSession();
                owner.log.Warning(ex, "Failed to start CrystalCast WebView2 window capture.");
                owner.nativeCaptureFailure = BrowserCaptureFailureDiagnostics.Format(ex);
                owner.browserStatus = $"WebView2 window capture failed, using JPEG fallback: {ex.GetBaseException().Message}";
            }

            EnsureCaptureLoopStarted();
        }

        private bool TryStartWindowCapture()
        {
            if (hostWindow == null)
            {
                owner.nativeCaptureFailure = "host window was not created";
                owner.browserStatus = "WebView2 window capture unavailable, using JPEG fallback: host window was not created";
                return false;
            }

            if (!WebView2WindowCaptureSession.IsSupported(out var captureStatus))
            {
                owner.nativeCaptureFailure = captureStatus;
                owner.browserStatus = $"{captureStatus}; using JPEG fallback";
                return false;
            }

            windowCaptureSession = new WebView2WindowCaptureSession(
                hostWindow.Hwnd,
                owner.Width,
                owner.Height,
                () => owner.captureEnabled,
                () => owner.FramesPerSecond,
                (sharedHandle, width, height) => owner.PublishNativeFrame(sharedHandle, width, height),
                value => owner.lastCaptureMilliseconds = value,
                value => owner.browserStatus = value,
                HandleWindowCaptureFatalError);
            windowCaptureSession.Start();
            return true;
        }

        private void HandleWindowCaptureFatalError(Exception exception)
        {
            owner.nativeCaptureFailure = BrowserCaptureFailureDiagnostics.Format(exception);
            owner.log.Warning(exception, "CrystalCast WebView2 WGC callback failed; switching to JPEG fallback.");
            Post(() =>
            {
                DisposeWindowCaptureSession();
                owner.browserStatus = $"WebView2 window capture device lost, using JPEG fallback: {exception.GetBaseException().Message}";
                EnsureCaptureLoopStarted();
                return Task.CompletedTask;
            });
        }

        public void AcknowledgeNativeFrame(IntPtr sharedHandle)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                Volatile.Read(ref windowCaptureSession)?.AcknowledgeSharedTexture(sharedHandle);
        }

        private void DisposeWindowCaptureSession()
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                windowCaptureSession?.Dispose();

            windowCaptureSession = null;
        }

        private void ApplyBrowserMute(bool audioEnabled, float volume)
        {
            if (webView == null)
                return;

            try
            {
                var muted = !audioEnabled || volume <= 0.001f;
                var mutedProperty = webView.GetType().GetProperty("IsMuted");
                if (mutedProperty?.CanWrite == true)
                    mutedProperty.SetValue(webView, muted);
            }
            catch
            {
                // Older WebView2 runtimes may not expose IsMuted; the player page still enforces mute via JS.
            }
        }

        private void EnsureCaptureLoopStarted()
        {
            if (captureLoopTask is not { IsCompleted: false })
                captureLoopTask = CaptureLoopAsync(shutdownCancellation.Token);
        }

        private async Task CaptureLoopAsync(CancellationToken cancellationToken)
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / owner.FramesPerSecond);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!owner.captureEnabled || webView == null)
                    {
                        await screencast.PauseAsync();
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    if (owner.captureMode == WebView2CaptureMode.WindowGraphicsCapture && windowCaptureSession != null)
                    {
                        await screencast.PauseAsync();
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    var captureLabel = owner.captureMode == WebView2CaptureMode.WindowGraphicsCapture
                        ? "WebView2 JPEG fallback capture"
                        : "WebView2 JPEG capture";
                    if (await screencast.KeepRunningAsync(webView, screencastSettings))
                    {
                        LogScreencastOutcome();
                        owner.lastCaptureMilliseconds = screencast.LastDecodeMilliseconds;
                        owner.browserStatus = $"{captureLabel} running; {screencast.Status}";
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    LogScreencastOutcome();
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await CaptureOnceAsync(webView);
                        sw.Stop();
                        owner.lastCaptureMilliseconds = sw.Elapsed.TotalMilliseconds;
                        var captureStatus = $"{captureLabel} running with screenshots ({screencast.FailureReason})";
                        owner.browserStatus = owner.FramesPerSecond > 30.0f
                            ? $"{captureStatus}; high FPS is best effort"
                            : captureStatus;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        sw.Stop();
                        owner.browserStatus = $"WebView2 capture failed: {ex.Message}";
                        await Task.Delay(500, cancellationToken);
                    }

                    var remaining = frameInterval - sw.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, cancellationToken);
                    else if (owner.FramesPerSecond > 30.0f)
                        await Task.Delay(1, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void LogScreencastOutcome()
        {
            if (screencastOutcomeLogged)
                return;

            screencastOutcomeLogged = true;
            if (screencast.IsRunning)
            {
                owner.log.Information(
                    "CrystalCast WebView2 JPEG capture uses the DevTools screencast ({Reason}, max {Width}x{Height}).",
                    screencastSettings.Reason,
                    screencastSettings.MaxWidth,
                    screencastSettings.MaxHeight);
            }
            else
            {
                owner.log.Warning(
                    "CrystalCast WebView2 JPEG capture uses screenshots: {Reason}",
                    screencast.FailureReason);
            }
        }

        private async Task CaptureOnceAsync(CoreWebView2 currentWebView)
        {
            var frame = await jpegCapture.CaptureAsync(currentWebView, Interlocked.Increment(ref owner.sequence));
            if (frame == null)
                return;

            try
            {
                owner.PublishFrame(frame);
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var page = owner.playerPage;
            if (page == null || !BrowserNavigationPolicy.IsExpectedMessageSource(args.Source, page.Url))
            {
                owner.playerState.PlayerStatus = "ignored browser message from an unexpected document";
                return;
            }

            if (!BrowserMessageValidator.TryParseAuthenticated(
                    args.WebMessageAsJson,
                    page.Nonce,
                    out var document,
                    out var error))
            {
                owner.playerState.PlayerStatus = $"invalid browser message: {error}";
                return;
            }

            using (document!)
                owner.UpdateFromWebMessage(document!.RootElement);
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            var page = owner.playerPage;
            if (page == null || !BrowserNavigationPolicy.IsAllowedProviderDocument(args.Uri, page.Url))
            {
                args.Cancel = true;
                owner.browserStatus = "blocked browser navigation outside the provider player";
            }
        }

        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var page = owner.playerPage;
            if (page == null
                || environment == null
                || !string.Equals(args.Request.Uri, page.Url, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var stream = new MemoryStream(page.Utf8Content.ToArray(), writable: false);
            args.Response = environment.CreateWebResourceResponse(
                stream,
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
                owner.browserStatus = $"WebView2 navigation failed: {args.WebErrorStatus}";
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            owner.browserStatus = $"WebView2 process failed: {args.ProcessFailedKind}";
        }
    }

}
