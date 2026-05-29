using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CrystalCast.Video;

internal sealed class WebView2BrowserFrameSource : IVideoFrameSource, INativeVideoFrameSource, IMediaPlaybackTelemetrySource, IMediaPlaybackController, IBrowserFrameSourceRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly BrowserSourceDescriptor descriptor;
    private readonly string input;
    private readonly IBrowserSourceReference source;
    private readonly bool isValidSource;
    private readonly bool autoplay;
    private readonly WebView2CaptureMode captureMode;
    private readonly object telemetryLock = new();
    private BrowserThread? browserThread;
    private VideoFrame? latestFrame;
    private NativeVideoFrame? latestNativeFrame;
    private MediaPlaybackTelemetry telemetry = new();
    private bool loop;
    private bool playlistAutoplayNext;
    private bool audioEnabled;
    private float volume;
    private float playbackRate;
    private volatile bool captureEnabled;
    private volatile string browserStatus = "stopped";
    private volatile string playerStatus = "player not ready";
    private long sequence;
    private long lastFrameUnixMs;
    private long captureWindowStartUnixMs;
    private int captureWindowFrames;
    private double measuredCaptureFps;
    private double lastCaptureMilliseconds;
    private float detectedVideoFps;

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
        WebView2CaptureMode captureMode = WebView2CaptureMode.PreviewJpeg)
    {
        this.descriptor = descriptor;
        this.input = input;
        isValidSource = descriptor.TryParse(input, out source);
        Width = Math.Clamp(width, 320, 3840);
        Height = Math.Clamp(height, 180, 2160);
        FramesPerSecond = Math.Clamp(captureFps, 1.0f, 120.0f);
        this.autoplay = autoplay;
        this.loop = loop;
        this.playlistAutoplayNext = playlistAutoplayNext;
        this.audioEnabled = audioEnabled;
        this.volume = QuantizeVolume(volume);
        this.playbackRate = ClampPlaybackRate(playbackRate);
        this.captureMode = captureMode;

        if (!isValidSource)
            browserStatus = descriptor.InvalidSourceMessage;

        UpdateTelemetry(ScreenPlaybackState.Stopped, 0, 0, this.playbackRate, string.Empty);
    }

    public BrowserSourceProviderKind ProviderKind => descriptor.ProviderKind;
    public string Name => captureMode == WebView2CaptureMode.WindowGraphicsCapture
        ? $"{descriptor.DisplayName} browser (WebView2 window capture)"
        : $"{descriptor.DisplayName} browser (WebView2 JPEG capture)";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; private set; }
    public bool IsRunning => captureEnabled;
    public float DetectedVideoFps => detectedVideoFps;

    public string Status
    {
        get
        {
            var frameAge = lastFrameUnixMs == 0
                ? "no captured frame"
                : $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs} ms frame age";
            return $"{browserStatus}; {playerStatus}; capture {measuredCaptureFps:0.#}/{FramesPerSecond:0.#} fps; {lastCaptureMilliseconds:0.#} ms; {frameAge}";
        }
    }

    public void Start()
    {
        var wasCaptureEnabled = captureEnabled;
        if (!isValidSource)
        {
            browserStatus = descriptor.ParseInvalidInputStatus(input);
            return;
        }

        if (browserThread == null)
        {
            if (!TryGetWebView2Runtime(out var runtimeVersion, out var runtimeError))
            {
                browserStatus = runtimeError;
                return;
            }

            browserStatus = $"starting WebView2 {runtimeVersion}";
            browserThread = new BrowserThread(this);
        }

        captureEnabled = true;
        if (!wasCaptureEnabled)
            browserThread.Play();
    }

    public void Stop()
    {
        var wasCaptureEnabled = captureEnabled;
        captureEnabled = false;
        if (wasCaptureEnabled)
            browserThread?.Pause();

        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), GetTelemetryDurationMs(), playbackRate, GetTelemetryTitle());
    }

    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        volume = QuantizeVolume(volume);
        playbackRate = ClampPlaybackRate(playbackRate);

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
        captureEnabled = true;
        browserThread?.Play();
    }

    public void Pause()
    {
        captureEnabled = false;
        browserThread?.Pause();
        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), GetTelemetryDurationMs(), playbackRate, GetTelemetryTitle());
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
        captureEnabled = true;
        browserThread?.Restart();
    }

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        frame = latestFrame!;
        return frame != null;
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

    public void Dispose()
    {
        captureEnabled = false;
        browserThread?.Dispose();
        browserThread = null;
        browserStatus = "disposed";
        UpdateTelemetry(ScreenPlaybackState.Stopped, GetTelemetryPositionMs(), GetTelemetryDurationMs(), playbackRate, GetTelemetryTitle());
    }

    public void UpdateCaptureFps(float fps)
    {
        var clamped = Math.Clamp(fps, 1.0f, 120.0f);
        if (Math.Abs(FramesPerSecond - clamped) < 0.01f)
            return;

        FramesPerSecond = clamped;
    }

    private string WritePlayerPage(string contentFolder)
    {
        const string playerPageName = "player.html";
        var playerPagePath = Path.Combine(contentFolder, playerPageName);
        File.WriteAllText(playerPagePath, descriptor.BuildHtml(source, new BrowserPlaybackSettings(autoplay, loop, playlistAutoplayNext, audioEnabled, volume, playbackRate)));
        return playerPageName;
    }

    private void PublishFrame(byte[] pixels, int width, int height)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = new VideoFrame(pixels, width, height, Interlocked.Increment(ref sequence), now);
        Interlocked.Exchange(ref latestFrame, frame);
        RecordCapturedFrame(now);
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
        if (!root.TryGetProperty("type", out var typeProperty))
            return;

        var type = typeProperty.GetString();
        switch (type)
        {
            case "ready":
                playerStatus = $"player ready: {source.DisplayName}";
                browserThread?.StartWindowCaptureWhenReady();
                if (autoplay && captureEnabled)
                    browserThread?.Play();
                break;
            case "status":
                UpdateFromStatusMessage(root);
                break;
            case "video-fps":
                UpdateDetectedVideoFps(root);
                break;
            case "error":
                playerStatus = descriptor.DescribeError(root);
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
        var currentVideoId = BrowserSourceDescriptors.TryGetString(root, "videoId", source.VideoId);
        var positionSeconds = BrowserSourceDescriptors.TryGetDouble(root, "positionSeconds", 0.0);
        var durationSeconds = BrowserSourceDescriptors.TryGetDouble(root, "durationSeconds", 0.0);
        var rate = (float)BrowserSourceDescriptors.TryGetDouble(root, "rate", playbackRate);
        var stateCode = BrowserSourceDescriptors.TryGetInt(root, "state", -1);
        var playbackState = stateCode switch
        {
            1 or 3 => ScreenPlaybackState.Playing,
            2 or 5 => ScreenPlaybackState.Paused,
            _ => ScreenPlaybackState.Stopped,
        };

        var positionMs = (long)Math.Max(0.0, positionSeconds * 1000.0);
        var durationMs = (long)Math.Max(0.0, durationSeconds * 1000.0);
        UpdateTelemetry(playbackState, positionMs, durationMs, rate, title, currentVideoId);
        playerStatus = descriptor.FormatPlayerStatus(title, stateCode, positionSeconds, durationSeconds, root);
    }

    private void UpdateTelemetry(ScreenPlaybackState state, long positionMs, long durationMs, float rate, string title, string currentVideoId = "")
    {
        if (!descriptor.IsValidVideoId(currentVideoId))
            currentVideoId = source.VideoId;

        lock (telemetryLock)
        {
            telemetry = new MediaPlaybackTelemetry
            {
                State = state,
                PositionMs = positionMs,
                DurationMs = durationMs,
                Rate = ClampPlaybackRate(rate),
                Title = title,
                VideoId = currentVideoId,
                CanonicalUrl = descriptor.BuildCanonicalSourceUrl(source, currentVideoId),
                HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DetectedVideoFps = detectedVideoFps,
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

    private void UpdateDetectedVideoFps(JsonElement root)
    {
        var fps = (float)BrowserSourceDescriptors.TryGetDouble(root, "fps", 0.0);
        if (fps >= 1.0f && fps <= 240.0f)
        {
            detectedVideoFps = fps;
            playerStatus = $"detected video fps: {fps:0.#}";
        }
    }

    internal static bool TryGetWebView2Runtime(out string runtimeVersion, out string error)
    {
        runtimeVersion = string.Empty;
        error = string.Empty;

        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrWhiteSpace(runtimeVersion))
                return true;

            error = "Microsoft Edge WebView2 Runtime is not installed";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Microsoft Edge WebView2 Runtime unavailable: {ex.Message}";
            return false;
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
        private readonly WebView2BrowserFrameSource owner;
        private readonly Thread thread;
        private readonly ManualResetEventSlim contextReady = new();
        private volatile bool shutdownRequested;
        private BrowserSynchronizationContext? synchronizationContext;
        private CoreWebView2Controller? controller;
        private CoreWebView2? webView;
        private WebView2HostWindow? hostWindow;
        private WebView2WindowCaptureSession? windowCaptureSession;
        private bool windowCaptureStartRequested;
        private volatile bool disposed;

        public BrowserThread(WebView2BrowserFrameSource owner)
        {
            this.owner = owner;
            thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = $"CrystalCast WebView2 {owner.descriptor.DisplayName} source",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Play()
        {
            Post(async () =>
            {
                if (webView != null)
                    await webView.ExecuteScriptAsync("window.crystalCastPlay && window.crystalCastPlay();");
            });
        }

        public void Pause()
        {
            Post(async () =>
            {
                if (webView != null)
                    await webView.ExecuteScriptAsync("window.crystalCastPause && window.crystalCastPause();");
            });
        }

        public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
        {
            var settingsJson = JsonSerializer.Serialize(new
            {
                audioEnabled,
                volume,
                playbackRate,
                loop,
                playlistAutoplayNext,
            }, JsonOptions);

            Post(async () =>
            {
                if (webView != null)
                {
                    ApplyBrowserMute(audioEnabled, volume);
                    await webView.ExecuteScriptAsync($"window.crystalCastApplySettings && window.crystalCastApplySettings({settingsJson});");
                }
            });
        }

        public void SeekBy(double seconds)
        {
            var secondsJson = JsonSerializer.Serialize(seconds, JsonOptions);
            Post(async () =>
            {
                if (webView != null)
                    await webView.ExecuteScriptAsync($"window.crystalCastSeekBy && window.crystalCastSeekBy({secondsJson});");
            });
        }

        public void SeekTo(double seconds)
        {
            var secondsJson = JsonSerializer.Serialize(seconds, JsonOptions);
            Post(async () =>
            {
                if (webView != null)
                    await webView.ExecuteScriptAsync($"window.crystalCastSeekTo && window.crystalCastSeekTo({secondsJson});");
            });
        }

        public void Restart()
        {
            Post(async () =>
            {
                if (webView != null)
                    await webView.ExecuteScriptAsync("window.crystalCastRestart && window.crystalCastRestart();");
            });
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            shutdownRequested = true;
            if (contextReady.Wait(TimeSpan.FromSeconds(1)))
                synchronizationContext?.Post(_ => shutdownRequested = true, null);

            if (!thread.Join(TimeSpan.FromSeconds(3)))
                owner.browserStatus = "WebView2 thread did not stop cleanly";

            contextReady.Dispose();
        }

        private void Post(Func<Task> action)
        {
            if (disposed || !contextReady.Wait(TimeSpan.FromSeconds(1)))
                return;

            synchronizationContext?.Post(state =>
            {
                _ = RunPostedActionAsync((Func<Task>)state!);
            }, action);
        }

        private async Task RunPostedActionAsync(Func<Task> action)
        {
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
            contextReady.Set();

            try
            {
                _ = InitializeAsync();
                while (!shutdownRequested)
                {
                    var shouldQuit = shutdownRequested;
                    BrowserNative.PumpMessages(ref shouldQuit);
                    shutdownRequested = shouldQuit;
                    context.ExecutePending();
                    BrowserNative.WaitForWork(context.WorkAvailable, 50);
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
                    webView = null;
                    DisposeWindowCaptureSession();
                    controller?.Close();
                    controller = null;
                    hostWindow?.Dispose();
                    hostWindow = null;
                }
                catch
                {
                    // Best-effort browser cleanup during plugin unload/source changes.
                }

                context.Dispose();
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CrystalCast",
                    owner.descriptor.WebView2UserDataFolderName);
                Directory.CreateDirectory(userDataFolder);
                var contentFolder = Path.Combine(userDataFolder, "Content");
                Directory.CreateDirectory(contentFolder);
                var playerPageName = owner.WritePlayerPage(contentFolder);

                var parentHwnd = BrowserNative.HwndMessage;
                if (owner.captureMode == WebView2CaptureMode.WindowGraphicsCapture)
                {
                    hostWindow = WebView2HostWindow.Create(owner.Width, owner.Height);
                    parentHwnd = hostWindow.Hwnd;
                }

                CoreWebView2Environment environment;
                if (string.IsNullOrWhiteSpace(owner.descriptor.WebView2AdditionalBrowserArguments))
                {
                    environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                }
                else
                {
                    var environmentOptions = new CoreWebView2EnvironmentOptions(
                        additionalBrowserArguments: owner.descriptor.WebView2AdditionalBrowserArguments);
                    environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, environmentOptions);
                }

                controller = await environment.CreateCoreWebView2ControllerAsync(parentHwnd);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, owner.Width, owner.Height);
                controller.IsVisible = true;
                controller.DefaultBackgroundColor = System.Drawing.Color.Black;
                hostWindow?.Show();

                webView = controller.CoreWebView2;
                ConfigureWebView(webView);
                ApplyBrowserMute(owner.audioEnabled, owner.volume);
                webView.WebMessageReceived += OnWebMessageReceived;
                webView.NavigationCompleted += OnNavigationCompleted;
                webView.ProcessFailed += OnProcessFailed;
                webView.SetVirtualHostNameToFolderMapping(
                    owner.descriptor.VirtualHostName,
                    contentFolder,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                owner.browserStatus = $"WebView2 loading {owner.descriptor.DisplayName} player";
                webView.Navigate($"{owner.descriptor.PlayerOrigin}/{playerPageName}");

                _ = CaptureLoopAsync();
            }
            catch (Exception ex)
            {
                owner.browserStatus = $"WebView2 init failed: {ex.Message}";
            }
        }

        public void StartWindowCaptureWhenReady()
        {
            if (owner.captureMode != WebView2CaptureMode.WindowGraphicsCapture || disposed || windowCaptureSession != null || windowCaptureStartRequested)
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
            try
            {
                if (TryStartWindowCapture())
                    return;
            }
            catch (Exception ex)
            {
                DisposeWindowCaptureSession();
                Plugin.Log.Warning(ex, "Failed to start CrystalCast WebView2 window capture.");
                owner.browserStatus = $"WebView2 window capture failed, using JPEG fallback: {ex.GetBaseException().Message}";
            }

            _ = CaptureLoopAsync();
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
                value => owner.browserStatus = value);
            windowCaptureSession.Start();
            return true;
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

        private async Task CaptureLoopAsync()
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / owner.FramesPerSecond);
            while (!shutdownRequested)
            {
                if (!owner.captureEnabled || webView == null)
                {
                    await Task.Delay(200);
                    continue;
                }

                if (owner.captureMode == WebView2CaptureMode.WindowGraphicsCapture && windowCaptureSession != null)
                {
                    await Task.Delay(200);
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
                catch (Exception ex)
                {
                    sw.Stop();
                    owner.browserStatus = $"WebView2 capture failed: {ex.Message}";
                    await Task.Delay(500);
                }

                var remaining = frameInterval - sw.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining);
                else if (owner.FramesPerSecond > 30.0f)
                    await Task.Delay(1);
            }
        }

        private async Task CaptureOnceAsync(CoreWebView2 currentWebView)
        {
            await using var stream = new MemoryStream();
            await currentWebView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            if (stream.Length == 0)
                return;

            stream.Position = 0;
            using var image = Image.Load<Bgra32>(stream);
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            owner.PublishFrame(pixels, image.Width, image.Height);
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                owner.UpdateFromWebMessage(document.RootElement);
            }
            catch (Exception ex)
            {
                owner.playerStatus = $"invalid browser message: {ex.Message}";
            }
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

    private sealed class BrowserSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        private readonly AutoResetEvent workAvailable = new(false);
        private bool disposed;

        public WaitHandle WorkAvailable => workAvailable;

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (disposed)
                return;

            queue.Enqueue((d, state));
            try
            {
                workAvailable.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void ExecutePending()
        {
            while (queue.TryDequeue(out var work))
                work.Callback(work.State);
        }

        public void Dispose()
        {
            disposed = true;
            workAvailable.Dispose();
        }
    }

    private static class BrowserNative
    {
        private const uint PmRemove = 0x0001;
        private const uint QsAllInput = 0x04FF;
        private const uint MwmoInputAvailable = 0x0004;
        private const uint WmQuit = 0x0012;

        public static readonly IntPtr HwndMessage = new(-3);

        public static void PumpMessages(ref bool quit)
        {
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
            {
                if (message.Message == WmQuit)
                {
                    quit = true;
                    return;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        public static void WaitForWork(WaitHandle handle, uint timeoutMs)
        {
            var handles = new[] { handle.SafeWaitHandle.DangerousGetHandle() };
            _ = MsgWaitForMultipleObjectsEx(1, handles, timeoutMs, QsAllInput, MwmoInputAvailable);
        }

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public System.Drawing.Point Point;
        }
    }
}
