using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Dalamud.Plugin.Services;

namespace CrystalCast.Video;

internal sealed class GenericWebBrowserFrameSource : IVideoFrameSource, INativeVideoFrameSource, IMediaPlaybackTelemetrySource, IMediaPlaybackController, IBrowserFrameSourceRuntime, IBrowserControlsHost
{
    private const string PlaybackUnavailableStatus = "Playback sync unavailable for this page";

    private readonly string input;
    private readonly GenericWebSourceReference source;
    private readonly bool isValidSource;
    private readonly BrowserPlaybackIntent playbackIntent;
    private readonly string messageNonce = BrowserPageMessaging.CreateNonce();
    private readonly WebView2CaptureMode captureMode;
    private readonly IPluginLog log;
    private readonly FrameCadenceDiagnostics cadenceDiagnostics = new();
    private readonly object telemetryLock = new();
    private BrowserThread? browserThread;
    private VideoFrame? latestFrame;
    private NativeVideoFrame? latestNativeFrame;
    private MediaPlaybackTelemetry telemetry = new();
    private bool loop;
    private bool audioEnabled;
    private float volume;
    private float playbackRate;
    private bool settingsPublished;
    private volatile bool captureEnabled;
    private volatile string browserStatus = "stopped";
    private volatile string playerStatus = "player not ready";
    private long sequence;
    private long lastFrameUnixMs;
    private long captureWindowStartUnixMs;
    private int captureWindowFrames;
    private double measuredCaptureFps;
    private double lastCaptureMilliseconds;
    private volatile bool disposed;

    public GenericWebBrowserFrameSource(
        string input,
        int width,
        int height,
        float captureFps,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate,
        WebView2CaptureMode captureMode,
        IPluginLog log)
    {
        this.input = input;
        isValidSource = GenericWebUrl.TryParseSource(input, out source);
        Width = Math.Clamp(width, 320, 3840);
        Height = Math.Clamp(height, 180, 2160);
        FramesPerSecond = Math.Clamp(captureFps, 1.0f, 120.0f);
        playbackIntent = new BrowserPlaybackIntent(autoplay);
        this.loop = loop;
        this.audioEnabled = audioEnabled;
        this.volume = QuantizeVolume(volume);
        this.playbackRate = ClampPlaybackRate(playbackRate);
        this.captureMode = captureMode;
        this.log = log;

        if (!isValidSource)
            browserStatus = BrowserSourceDescriptors.GenericWeb.InvalidSourceMessage;

        UpdateTelemetry(ScreenPlaybackState.Stopped, 0, 0, this.playbackRate, string.Empty, input);
    }

    public BrowserSourceProviderKind ProviderKind => BrowserSourceProviderKind.GenericWeb;
    public string Name => captureMode == WebView2CaptureMode.WindowGraphicsCapture
        ? "Generic Web browser (WebView2 window capture)"
        : "Generic Web browser (WebView2 JPEG capture)";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; private set; }
    public bool IsRunning => captureEnabled;
    public float DetectedVideoFps => 0.0f;
    public bool BrowserControlsAvailable => isValidSource;
    public bool BrowserControlsVisible => browserThread?.BrowserControlsVisible == true;

    public string Status
    {
        get
        {
            var frameAge = lastFrameUnixMs == 0
                ? "no captured frame"
                : $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs} ms frame age";
            return $"capture {measuredCaptureFps:0.#}/{FramesPerSecond:0.#} fps; {cadenceDiagnostics.Status}; {lastCaptureMilliseconds:0.#} ms; {frameAge}; {browserStatus}; {playerStatus}";
        }
    }

    public void Start()
    {
        var wasCaptureEnabled = captureEnabled;
        if (!isValidSource)
        {
            browserStatus = BrowserSourceDescriptors.GenericWeb.ParseInvalidInputStatus(input);
            return;
        }

        EnsureBrowserThread();
        if (browserThread == null)
            return;

        captureEnabled = true;
        if (!wasCaptureEnabled && playbackIntent.IsPlayRequested)
            browserThread.Play();
    }

    public void Stop()
    {
        var wasCaptureEnabled = captureEnabled;
        captureEnabled = false;
        if (wasCaptureEnabled)
            browserThread?.Pause();

        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), GetTelemetryDurationMs(), playbackRate, GetTelemetryTitle(), GetTelemetryUrl());
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

    public bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry currentTelemetry)
    {
        lock (telemetryLock)
        {
            currentTelemetry = telemetry;
        }

        return isValidSource;
    }

    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        volume = QuantizeVolume(volume);
        playbackRate = ClampPlaybackRate(playbackRate);

        if (settingsPublished
            && this.audioEnabled == audioEnabled
            && Math.Abs(this.volume - volume) < 0.001f
            && Math.Abs(this.playbackRate - playbackRate) < 0.001f
            && this.loop == loop)
        {
            return;
        }

        this.audioEnabled = audioEnabled;
        this.volume = volume;
        this.playbackRate = playbackRate;
        this.loop = loop;
        browserThread?.ApplyPlaybackSettings(audioEnabled, volume, playbackRate, loop);
        settingsPublished = true;
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
        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), GetTelemetryDurationMs(), playbackRate, GetTelemetryTitle(), GetTelemetryUrl());
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

    public void UpdateCaptureFps(float fps)
    {
        var clamped = Math.Clamp(fps, 1.0f, 120.0f);
        if (Math.Abs(FramesPerSecond - clamped) < 0.01f)
            return;

        FramesPerSecond = clamped;
    }

    public bool ShowBrowserControls()
    {
        if (!isValidSource)
        {
            browserStatus = BrowserSourceDescriptors.GenericWeb.ParseInvalidInputStatus(input);
            return false;
        }

        EnsureBrowserThread();
        if (browserThread == null)
            return false;

        return browserThread.ShowBrowserControls();
    }

    public bool HideBrowserControls()
    {
        if (browserThread == null)
            return false;

        browserThread.HideBrowserControls();
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        captureEnabled = false;
        Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        browserThread?.Dispose();
        browserThread = null;
        browserStatus = "disposed";
        UpdateTelemetry(ScreenPlaybackState.Stopped, GetTelemetryPositionMs(), GetTelemetryDurationMs(), playbackRate, GetTelemetryTitle(), GetTelemetryUrl());
    }

    private void EnsureBrowserThread()
    {
        if (browserThread != null || !isValidSource)
            return;

        if (!WebView2BrowserFrameSource.TryGetWebView2Runtime(out var runtimeVersion, out var runtimeError))
        {
            browserStatus = runtimeError;
            return;
        }

        browserStatus = $"starting WebView2 {runtimeVersion}";
        browserThread = new BrowserThread(this);
        settingsPublished = false;
        ApplyPlaybackSettings(audioEnabled, volume, playbackRate, loop, true);
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
        if (!BrowserPageMessaging.HasNonce(root, messageNonce)
            || !root.TryGetProperty("type", out var typeProperty))
            return;

        var type = typeProperty.GetString();
        switch (type)
        {
            case "ready":
                playerStatus = "media controller ready";
                browserThread?.MarkPlayerReady();
                browserThread?.ApplyPlaybackSettings(audioEnabled, volume, playbackRate, loop);
                settingsPublished = true;
                if (playbackIntent.IsPlayRequested && captureEnabled)
                    browserThread?.Play();
                break;
            case "status":
                UpdateFromStatusMessage(root);
                break;
            case "script-error":
                playerStatus = BrowserSourceDescriptors.TryGetString(root, "message", "script error");
                break;
            case "debug":
                playerStatus = BrowserSourceDescriptors.TryGetString(root, "message", "browser debug");
                break;
        }
    }

    private void UpdateFromStatusMessage(JsonElement root)
    {
        var title = BrowserSourceDescriptors.TryGetString(root, "title", string.Empty);
        var url = BrowserSourceDescriptors.TryGetString(root, "url", source.Url, BrowserUriPolicy.MaximumUrlLength);
        var noMedia = BrowserSourceDescriptors.TryGetBool(root, "noMedia", false);
        var positionSeconds = BrowserSourceDescriptors.TryGetDouble(root, "positionSeconds", 0.0);
        var durationSeconds = BrowserSourceDescriptors.TryGetDouble(root, "durationSeconds", 0.0);
        var rate = (float)BrowserSourceDescriptors.TryGetDouble(root, "rate", playbackRate);
        var stateCode = BrowserSourceDescriptors.TryGetInt(root, "state", 0);
        var playbackState = noMedia
            ? ScreenPlaybackState.Stopped
            : stateCode switch
            {
                1 => ScreenPlaybackState.Playing,
                2 => ScreenPlaybackState.Paused,
                _ => ScreenPlaybackState.Stopped,
            };

        var positionMs = BrowserMessageValidator.ToBoundedMilliseconds(positionSeconds);
        var durationMs = BrowserMessageValidator.ToBoundedMilliseconds(durationSeconds);
        UpdateTelemetry(playbackState, positionMs, durationMs, rate, title, url);
        playerStatus = noMedia
            ? PlaybackUnavailableStatus
            : string.IsNullOrWhiteSpace(title)
                ? $"media state {stateCode}; {positionSeconds:0.0}s / {durationSeconds:0.0}s"
                : $"{title}; state {stateCode}; {positionSeconds:0.0}s / {durationSeconds:0.0}s";
    }

    private void UpdateTelemetry(ScreenPlaybackState state, long positionMs, long durationMs, float rate, string title, string url)
    {
        if (!GenericWebUrl.TryParseSource(url, out var parsed))
            parsed = source;

        lock (telemetryLock)
        {
            telemetry = new MediaPlaybackTelemetry
            {
                State = state,
                PositionMs = positionMs,
                DurationMs = durationMs,
                Rate = ClampPlaybackRate(rate),
                Title = title,
                VideoId = string.Empty,
                CanonicalUrl = parsed.Url,
                HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DetectedVideoFps = 0.0f,
            };
        }
    }

    private string GetTelemetryTitle()
    {
        lock (telemetryLock)
        {
            return telemetry.Title;
        }
    }

    private string GetTelemetryUrl()
    {
        lock (telemetryLock)
        {
            return string.IsNullOrWhiteSpace(telemetry.CanonicalUrl) ? source.Url : telemetry.CanonicalUrl;
        }
    }

    private long GetTelemetryPositionMs()
    {
        lock (telemetryLock)
        {
            return telemetry.PositionMs;
        }
    }

    private long GetTelemetryDurationMs()
    {
        lock (telemetryLock)
        {
            return telemetry.DurationMs;
        }
    }

    private static float ClampPlaybackRate(float rate)
    {
        if (!float.IsFinite(rate))
            return 1.0f;

        return Math.Clamp(rate, 0.25f, 2.0f);
    }

    private static float ClampVolume(float volume)
    {
        if (!float.IsFinite(volume))
            return 0.0f;

        return Math.Clamp(volume, 0.0f, 1.0f);
    }

    private static float QuantizeVolume(float volume)
    {
        var clamped = ClampVolume(volume);
        return clamped <= 0.0f
            ? 0.0f
            : MathF.Ceiling(clamped * 100.0f) / 100.0f;
    }

    private sealed class BrowserThread : IDisposable
    {
        private readonly GenericWebBrowserFrameSource owner;
        private readonly Thread thread;
        private readonly BrowserLifecycle lifecycle = new();
        private readonly CancellationTokenSource shutdownCancellation = new();
        private readonly ConcurrentQueue<Func<Task>> pendingActions = new();
        private readonly Queue<string> pendingPlayerMessages = new();
        private readonly WebView2JpegFrameCapture jpegCapture = new();
        private volatile bool shutdownRequested;
        private BrowserSynchronizationContext? synchronizationContext;
        private CoreWebView2Controller? controller;
        private CoreWebView2? webView;
        private WebView2HostWindow? hostWindow;
        private WebView2WindowCaptureSession? windowCaptureSession;
        private Task? captureLoopTask;
        private bool playerReady;
        private volatile bool browserControlsVisible;
        private long interactionDismissedAtTick;

        public BrowserThread(GenericWebBrowserFrameSource owner)
        {
            this.owner = owner;
            lifecycle.TryStart();
            thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "CrystalCast WebView2 Generic Web source",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public bool BrowserControlsVisible => browserControlsVisible;

        public void Play()
        {
            PostPlayerMessage(BrowserPageMessaging.Play(owner.messageNonce));
        }

        public void Pause()
        {
            PostPlayerMessage(BrowserPageMessaging.Pause(owner.messageNonce));
        }

        public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop)
        {
            var message = BrowserPageMessaging.Settings(owner.messageNonce, audioEnabled, volume, playbackRate, loop, playlistAutoplayNext: true);
            Post(() =>
            {
                ApplyBrowserMute(audioEnabled, volume);
                SendOrQueuePlayerMessage(message);
                return Task.CompletedTask;
            });
        }

        public void SeekBy(double seconds)
        {
            PostPlayerMessage(BrowserPageMessaging.SeekBy(owner.messageNonce, seconds));
        }

        public void SeekTo(double seconds)
        {
            PostPlayerMessage(BrowserPageMessaging.SeekTo(owner.messageNonce, seconds));
        }

        public void Restart()
        {
            PostPlayerMessage(BrowserPageMessaging.Restart(owner.messageNonce));
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
                while (!shutdownRequested)
                {
                    var shouldQuit = shutdownRequested;
                    BrowserNativeMessagePump.PumpMessages(ref shouldQuit);
                    shutdownRequested = shouldQuit;
                    context.ExecutePending();
                    hostWindow?.PollInteractionFocus();
                    BrowserNativeMessagePump.WaitForWork(context.WorkAvailable, 50);
                }

                var cleanupDeadline = Environment.TickCount64 + 3000;
                while ((initializationTask is { IsCompleted: false } || captureLoopTask is { IsCompleted: false })
                    && Environment.TickCount64 < cleanupDeadline)
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
                try
                {
                    if (webView != null)
                    {
                        webView.WebMessageReceived -= OnWebMessageReceived;
                        webView.NavigationStarting -= OnNavigationStarting;
                        webView.NavigationCompleted -= OnNavigationCompleted;
                        webView.ProcessFailed -= OnProcessFailed;
                        webView.NewWindowRequested -= OnNewWindowRequested;
                        webView.PermissionRequested -= OnPermissionRequested;
                        webView.DownloadStarting -= OnDownloadStarting;
                    }
                    webView = null;
                    DisposeWindowCaptureSession();
                    controller?.Close();
                    controller = null;
                    if (hostWindow != null)
                        hostWindow.InteractionDismissed -= OnInteractionDismissed;
                    hostWindow?.Dispose();
                    hostWindow = null;
                }
                catch
                {
                }

                lifecycle.MarkStopped();
                jpegCapture.Dispose();
                shutdownCancellation.Dispose();
                context.Dispose();
            }
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var userDataFolder = BrowserProfileManager.GetWebView2UserDataFolder(BrowserSourceProviderKind.GenericWeb);
                Directory.CreateDirectory(userDataFolder);
                cancellationToken.ThrowIfCancellationRequested();

                var environmentOptions = new CoreWebView2EnvironmentOptions(
                    additionalBrowserArguments: "--autoplay-policy=no-user-gesture-required");
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, environmentOptions);
                cancellationToken.ThrowIfCancellationRequested();

                hostWindow = WebView2HostWindow.Create(owner.Width, owner.Height);
                hostWindow.InteractionDismissed += OnInteractionDismissed;
                var parentHwnd = hostWindow.Hwnd;

                controller = await environment.CreateCoreWebView2ControllerAsync(parentHwnd);
                cancellationToken.ThrowIfCancellationRequested();
                controller.Bounds = new System.Drawing.Rectangle(0, 0, owner.Width, owner.Height);
                controller.IsVisible = true;
                hostWindow.ShowForCapture();

                webView = controller.CoreWebView2;
                ConfigureWebView(webView);
                ApplyBrowserMute(owner.audioEnabled, owner.volume);
                webView.WebMessageReceived += OnWebMessageReceived;
                webView.NavigationStarting += OnNavigationStarting;
                webView.NavigationCompleted += OnNavigationCompleted;
                webView.ProcessFailed += OnProcessFailed;
                webView.NewWindowRequested += OnNewWindowRequested;
                webView.PermissionRequested += OnPermissionRequested;
                webView.DownloadStarting += OnDownloadStarting;
                await webView.AddScriptToExecuteOnDocumentCreatedAsync(BuildControllerScript(owner.messageNonce));
                cancellationToken.ThrowIfCancellationRequested();
                playerReady = false;
                owner.browserStatus = "WebView2 loading Generic Web page";
                webView.Navigate(owner.source.Url);

                if (!lifecycle.TryMarkRunning())
                    cancellationToken.ThrowIfCancellationRequested();

                if (owner.captureMode == WebView2CaptureMode.WindowGraphicsCapture)
                    StartWindowCaptureOrFallback();
                else
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

        private void StartWindowCaptureOrFallback()
        {
            try
            {
                if (TryStartWindowCapture())
                    return;
            }
            catch (Exception ex)
            {
                DisposeWindowCaptureSession();
                owner.log.Warning(ex, "Failed to start CrystalCast Generic Web window capture.");
                owner.browserStatus = $"WebView2 window capture failed, using JPEG fallback: {ex.GetBaseException().Message}";
            }

            EnsureCaptureLoopStarted();
        }

        private bool TryStartWindowCapture()
        {
            if (hostWindow == null)
            {
                owner.browserStatus = "WebView2 window capture unavailable, using JPEG fallback: host window was not created";
                return false;
            }

            if (!WebView2WindowCaptureSession.IsSupported(out var captureStatus))
            {
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
            Post(() =>
            {
                DisposeWindowCaptureSession();
                owner.browserStatus = $"WebView2 window capture device lost, using JPEG fallback: {exception.GetBaseException().Message}";
                EnsureCaptureLoopStarted();
                return Task.CompletedTask;
            });
        }

        private void DisposeWindowCaptureSession()
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                windowCaptureSession?.Dispose();

            windowCaptureSession = null;
        }

        private static void ConfigureWebView(CoreWebView2 webView)
        {
            webView.Settings.AreDefaultContextMenusEnabled = false;
            webView.Settings.AreDevToolsEnabled = false;
            webView.Settings.AreBrowserAcceleratorKeysEnabled = false;
            webView.Settings.IsStatusBarEnabled = false;
            webView.Settings.IsZoomControlEnabled = false;
            webView.Settings.AreHostObjectsAllowed = false;
            webView.Settings.IsGeneralAutofillEnabled = false;
            webView.Settings.IsPasswordAutosaveEnabled = false;
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
            }
        }

        private void EnsureCaptureLoopStarted()
        {
            if (captureLoopTask is not { IsCompleted: false })
                captureLoopTask = CaptureLoopAsync(shutdownCancellation.Token);
        }

        private async Task CaptureLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frameInterval = TimeSpan.FromSeconds(1.0 / owner.FramesPerSecond);
                    if (!owner.captureEnabled || webView == null)
                    {
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await CaptureOnceAsync(webView);
                        sw.Stop();
                        owner.lastCaptureMilliseconds = sw.Elapsed.TotalMilliseconds;
                        var captureStatus = owner.captureMode == WebView2CaptureMode.WindowGraphicsCapture
                            ? "WebView2 JPEG fallback capture running"
                            : "WebView2 JPEG capture running";
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

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
            {
                owner.browserStatus = $"WebView2 navigation failed: {args.WebErrorStatus}";
                return;
            }

            owner.browserStatus = "WebView2 Generic Web page loaded";
            try
            {
                if (webView != null)
                {
                    await webView.ExecuteScriptAsync(BuildControllerScript(owner.messageNonce));
                }
            }
            catch (Exception ex)
            {
                owner.playerStatus = $"controller injection failed: {ex.Message}";
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            playerReady = false;
            if (!BrowserNavigationPolicy.IsAllowedGenericDocument(args.Uri))
            {
                args.Cancel = true;
                owner.browserStatus = "blocked non-HTTP browser navigation";
            }
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            owner.browserStatus = $"WebView2 process failed: {args.ProcessFailedKind}";
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var expectedSource = webView?.Source;
            if (!BrowserNavigationPolicy.IsExpectedMessageSource(args.Source, expectedSource))
            {
                owner.playerStatus = "ignored browser message from an unexpected document";
                return;
            }

            if (!BrowserMessageValidator.TryParseAuthenticated(
                    args.WebMessageAsJson,
                    owner.messageNonce,
                    out var document,
                    out var error))
            {
                owner.playerStatus = $"invalid browser message: {error}";
                return;
            }

            using (document!)
                owner.UpdateFromWebMessage(document!.RootElement);
        }

        private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
        }

        private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
        {
            args.State = BrowserPermissionPolicy.IsAllowed(args.PermissionKind.ToString())
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        }

        private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
        {
            args.Cancel = true;
        }
    }

    private static string BuildControllerScript(string nonce)
    {
        var script = """
(() => {
  try {
    if (window.__crystalCastGenericWebInstalled || window.top !== window) {
      return;
    }
    window.__crystalCastGenericWebInstalled = true;

    const config = {
      audioEnabled: true,
      volume: 0.7,
      playbackRate: 1.0,
      loop: false
    };

    function post(type, data) {
      try {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(Object.assign({ type }, data || {}));
        }
      } catch (_) {
      }
    }

    function collectMedia(targetWindow, depth, output) {
      if (!targetWindow || depth > 8) {
        return output;
      }

      let doc;
      try {
        doc = targetWindow.document;
      } catch (_) {
        return output;
      }

      if (!doc) {
        return output;
      }

      try {
        doc.querySelectorAll("video,audio").forEach((media) => output.push(media));
      } catch (_) {
      }

      try {
        doc.querySelectorAll("iframe,frame").forEach((frame) => {
          try {
            collectMedia(frame.contentWindow, depth + 1, output);
          } catch (_) {
          }
        });
      } catch (_) {
      }

      return output;
    }

    function isUsable(media) {
      return !!media && typeof media.play === "function" && typeof media.pause === "function";
    }

    function visibleArea(media) {
      if (!media || media.tagName !== "VIDEO") {
        return 0;
      }

      try {
        const rect = media.getBoundingClientRect();
        const style = media.ownerDocument.defaultView.getComputedStyle(media);
        if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) {
          return 0;
        }

        return Math.max(0, rect.width) * Math.max(0, rect.height);
      } catch (_) {
        return 0;
      }
    }

    function findMedia() {
      const media = collectMedia(window, 0, []).filter(isUsable);
      if (media.length === 0) {
        return null;
      }

      const playing = media.find((item) => !item.paused && !item.ended);
      if (playing) {
        return playing;
      }

      const videos = media
        .filter((item) => item.tagName === "VIDEO")
        .map((item) => ({ item, area: visibleArea(item) }))
        .sort((a, b) => b.area - a.area);
      if (videos.length > 0 && videos[0].area > 0) {
        return videos[0].item;
      }

      return media[0];
    }

    function finiteNumber(value, fallback) {
      return Number.isFinite(Number(value)) ? Number(value) : fallback;
    }

    function applySettings(media) {
      if (!media) {
        return;
      }

      try {
        media.muted = !config.audioEnabled || config.volume <= 0.001;
        media.volume = Math.max(0, Math.min(1, config.volume));
      } catch (_) {
      }

      try {
        media.playbackRate = Math.max(0.25, Math.min(2.0, config.playbackRate));
      } catch (_) {
      }

      try {
        media.loop = !!config.loop;
      } catch (_) {
      }
    }

    function seekTo(media, seconds) {
      if (!media || !Number.isFinite(Number(seconds))) {
        return;
      }

      try {
        const target = Math.max(0, Number(seconds));
        const duration = finiteNumber(media.duration, 0);
        media.currentTime = duration > 0 ? Math.min(target, duration) : target;
      } catch (_) {
      }
    }

    function publishStatus() {
      const media = findMedia();
      const title = document.title || "";
      const url = location.href || "";
      if (!media) {
        post("status", {
          noMedia: true,
          title,
          url,
          state: 0,
          positionSeconds: 0,
          durationSeconds: 0,
          rate: config.playbackRate
        });
        return;
      }

      applySettings(media);
      const duration = finiteNumber(media.duration, 0);
      const position = finiteNumber(media.currentTime, 0);
      const rate = finiteNumber(media.playbackRate, config.playbackRate);
      const state = media.ended ? 0 : (media.paused ? 2 : 1);
      post("status", {
        noMedia: false,
        title,
        url,
        state,
        positionSeconds: position,
        durationSeconds: duration,
        rate
      });
    }

    window.crystalCastPlay = function () {
      const media = findMedia();
      if (!media) {
        publishStatus();
        return;
      }

      applySettings(media);
      try {
        const result = media.play();
        if (result && typeof result.catch === "function") {
          result.catch(() => publishStatus());
        }
      } catch (_) {
      }
      publishStatus();
    };

    window.crystalCastPause = function () {
      const media = findMedia();
      if (media) {
        try {
          media.pause();
        } catch (_) {
        }
      }
      publishStatus();
    };

    window.crystalCastApplySettings = function (settings) {
      if (settings) {
        config.audioEnabled = !!settings.audioEnabled;
        config.volume = Math.max(0, Math.min(1, finiteNumber(settings.volume, config.volume)));
        config.playbackRate = Math.max(0.25, Math.min(2.0, finiteNumber(settings.playbackRate, config.playbackRate)));
        config.loop = !!settings.loop;
      }

      applySettings(findMedia());
      publishStatus();
    };

    window.crystalCastSeekBy = function (seconds) {
      const media = findMedia();
      if (media) {
        seekTo(media, finiteNumber(media.currentTime, 0) + finiteNumber(seconds, 0));
      }
      publishStatus();
    };

    window.crystalCastSeekTo = function (seconds) {
      seekTo(findMedia(), seconds);
      publishStatus();
    };

    window.crystalCastRestart = function () {
      const media = findMedia();
      if (media) {
        seekTo(media, 0);
        window.crystalCastPlay();
      } else {
        publishStatus();
      }
    };

    window.addEventListener("error", (event) => {
      post("script-error", { message: event && event.message ? event.message : "Generic Web script error" });
    });

    post("ready", { url: location.href || "" });
    publishStatus();
    setInterval(publishStatus, 500);
  } catch (error) {
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({
          type: "script-error",
          message: error && error.message ? error.message : String(error)
        });
      }
    } catch (_) {
    }
  }
})();
""";
        var nonceJson = JsonSerializer.Serialize(nonce);
        script = script.Replace(
            "Object.assign({ type }, data || {})",
            $"Object.assign({{ type, nonce: {nonceJson} }}, data || {{}})",
            StringComparison.Ordinal);
        return script + "\n" + BrowserPageMessaging.BuildCommandBridge(nonce);
    }
}
