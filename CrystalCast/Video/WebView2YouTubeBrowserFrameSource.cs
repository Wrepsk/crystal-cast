using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CrystalCast.Video;

public sealed class WebView2YouTubeBrowserFrameSource : IVideoFrameSource, IMediaPlaybackTelemetrySource, IMediaPlaybackController
{
    private const string VirtualHostName = "crystalcast.local";
    private const string PlayerOrigin = $"https://{VirtualHostName}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string input;
    private readonly string videoId;
    private readonly string canonicalUrl;
    private readonly bool isValidVideoId;
    private readonly bool autoplay;
    private readonly object telemetryLock = new();
    private BrowserThread? browserThread;
    private VideoFrame? latestFrame;
    private MediaPlaybackTelemetry telemetry = new();
    private bool loop;
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

    public WebView2YouTubeBrowserFrameSource(
        string input,
        int width,
        int height,
        float captureFps,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate)
    {
        this.input = input;
        isValidVideoId = YouTubeVideoId.TryParse(input, out videoId);
        canonicalUrl = YouTubeVideoId.BuildCanonicalWatchUrl(videoId);
        Width = Math.Clamp(width, 320, 3840);
        Height = Math.Clamp(height, 180, 2160);
        FramesPerSecond = Math.Clamp(captureFps, 1.0f, 60.0f);
        this.autoplay = autoplay;
        this.loop = loop;
        this.audioEnabled = audioEnabled;
        this.volume = ClampVolume(volume);
        this.playbackRate = ClampPlaybackRate(playbackRate);

        if (!isValidVideoId)
            browserStatus = "invalid YouTube URL or video ID";

        UpdateTelemetry(ScreenPlaybackState.Stopped, 0, this.playbackRate, string.Empty);
    }

    public string Name => "YouTube browser (WebView2 capture)";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; }
    public bool IsRunning => captureEnabled;

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
        if (!isValidVideoId)
        {
            browserStatus = $"invalid YouTube URL or video ID: {input}";
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

        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), playbackRate, GetTelemetryTitle());
    }

    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop)
    {
        volume = QuantizeVolume(volume);
        playbackRate = ClampPlaybackRate(playbackRate);

        if (this.audioEnabled == audioEnabled
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
        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), playbackRate, GetTelemetryTitle());
    }

    public void SeekBy(double seconds)
    {
        browserThread?.SeekBy(seconds);
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

    public bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry currentTelemetry)
    {
        lock (telemetryLock)
        {
            currentTelemetry = telemetry;
        }

        return isValidVideoId;
    }

    public void Dispose()
    {
        captureEnabled = false;
        browserThread?.Dispose();
        browserThread = null;
        browserStatus = "disposed";
        UpdateTelemetry(ScreenPlaybackState.Stopped, GetTelemetryPositionMs(), playbackRate, GetTelemetryTitle());
    }

    private string WritePlayerPage(string contentFolder)
    {
        const string playerPageName = "player.html";
        var playerPagePath = Path.Combine(contentFolder, playerPageName);
        File.WriteAllText(playerPagePath, YouTubePlayerPage.BuildHtml(videoId, autoplay, loop, audioEnabled, volume, playbackRate));
        return playerPageName;
    }

    private void PublishFrame(byte[] pixels, int width, int height)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = new VideoFrame(pixels, width, height, Interlocked.Increment(ref sequence), now);
        Interlocked.Exchange(ref latestFrame, frame);
        lastFrameUnixMs = now;

        if (captureWindowStartUnixMs == 0)
            captureWindowStartUnixMs = now;

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
                playerStatus = $"player ready: {videoId}";
                break;
            case "status":
                UpdateFromStatusMessage(root);
                break;
            case "error":
                playerStatus = DescribeYouTubeError(root);
                break;
            case "script-error":
                playerStatus = TryGetString(root, "message", "script error");
                break;
        }
    }

    private void UpdateFromStatusMessage(JsonElement root)
    {
        var title = TryGetString(root, "title", string.Empty);
        var positionSeconds = TryGetDouble(root, "positionSeconds", 0.0);
        var rate = (float)TryGetDouble(root, "rate", playbackRate);
        var stateCode = TryGetInt(root, "state", -1);
        var playbackState = stateCode switch
        {
            1 or 3 => ScreenPlaybackState.Playing,
            2 or 5 => ScreenPlaybackState.Paused,
            _ => ScreenPlaybackState.Stopped,
        };

        var positionMs = (long)Math.Max(0.0, positionSeconds * 1000.0);
        UpdateTelemetry(playbackState, positionMs, rate, title);
        playerStatus = string.IsNullOrWhiteSpace(title)
            ? $"player state {stateCode}; {positionSeconds:0.0}s"
            : $"{title}; state {stateCode}; {positionSeconds:0.0}s";
    }

    private void UpdateTelemetry(ScreenPlaybackState state, long positionMs, float rate, string title)
    {
        lock (telemetryLock)
        {
            telemetry = new MediaPlaybackTelemetry
            {
                State = state,
                PositionMs = positionMs,
                Rate = ClampPlaybackRate(rate),
                Title = title,
                VideoId = videoId,
                CanonicalUrl = canonicalUrl,
                HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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

    private static bool TryGetWebView2Runtime(out string runtimeVersion, out string error)
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

    private static string DescribeYouTubeError(JsonElement root)
    {
        if (root.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String)
            return messageProperty.GetString() ?? "YouTube player error";

        var code = TryGetInt(root, "code", 0);
        return code switch
        {
            2 => "YouTube player error: invalid video ID or parameter",
            5 => "YouTube player error: HTML5 playback failed",
            100 => "YouTube player error: video unavailable or private",
            101 or 150 => "YouTube player error: embedding is disallowed by the owner",
            153 => "YouTube player error: missing or blocked HTTP Referer",
            -1 => "YouTube player error: failed to load the IFrame API",
            _ => $"YouTube player error: {code}",
        };
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
        return MathF.Round(ClampVolume(volume) * 100.0f) / 100.0f;
    }

    private static string TryGetString(JsonElement root, string propertyName, string fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static double TryGetDouble(JsonElement root, string propertyName, double fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : fallback;
    }

    private static int TryGetInt(JsonElement root, string propertyName, int fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    private sealed class BrowserThread : IDisposable
    {
        private readonly WebView2YouTubeBrowserFrameSource owner;
        private readonly Thread thread;
        private readonly ManualResetEventSlim contextReady = new();
        private volatile bool shutdownRequested;
        private BrowserSynchronizationContext? synchronizationContext;
        private CoreWebView2Controller? controller;
        private CoreWebView2? webView;
        private volatile bool disposed;

        public BrowserThread(WebView2YouTubeBrowserFrameSource owner)
        {
            this.owner = owner;
            thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "CrystalCast WebView2 YouTube source",
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

        public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop)
        {
            var settingsJson = JsonSerializer.Serialize(new
            {
                audioEnabled,
                volume,
                playbackRate,
                loop,
            }, JsonOptions);

            Post(async () =>
            {
                if (webView != null)
                    await webView.ExecuteScriptAsync($"window.crystalCastApplySettings && window.crystalCastApplySettings({settingsJson});");
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
                    controller?.Close();
                    controller = null;
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
                    "WebView2");
                Directory.CreateDirectory(userDataFolder);
                var contentFolder = Path.Combine(userDataFolder, "Content");
                Directory.CreateDirectory(contentFolder);
                var playerPageName = owner.WritePlayerPage(contentFolder);

                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                controller = await environment.CreateCoreWebView2ControllerAsync(BrowserNative.HwndMessage);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, owner.Width, owner.Height);
                controller.IsVisible = true;

                webView = controller.CoreWebView2;
                ConfigureWebView(webView);
                webView.WebMessageReceived += OnWebMessageReceived;
                webView.NavigationCompleted += OnNavigationCompleted;
                webView.ProcessFailed += OnProcessFailed;
                webView.SetVirtualHostNameToFolderMapping(
                    VirtualHostName,
                    contentFolder,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                owner.browserStatus = "WebView2 loading YouTube player";
                webView.Navigate($"{PlayerOrigin}/{playerPageName}");
                _ = CaptureLoopAsync();
            }
            catch (Exception ex)
            {
                owner.browserStatus = $"WebView2 init failed: {ex.Message}";
            }
        }

        private static void ConfigureWebView(CoreWebView2 webView)
        {
            webView.Settings.AreDefaultContextMenusEnabled = false;
            webView.Settings.AreDevToolsEnabled = false;
            webView.Settings.AreBrowserAcceleratorKeysEnabled = false;
            webView.Settings.IsStatusBarEnabled = false;
            webView.Settings.IsZoomControlEnabled = false;
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

                var sw = Stopwatch.StartNew();
                try
                {
                    await CaptureOnceAsync(webView);
                    sw.Stop();
                    owner.lastCaptureMilliseconds = sw.Elapsed.TotalMilliseconds;
                    owner.browserStatus = owner.FramesPerSecond > 30.0f
                        ? "WebView2 JPEG capture running; high FPS is best effort"
                        : "WebView2 JPEG capture running";
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
