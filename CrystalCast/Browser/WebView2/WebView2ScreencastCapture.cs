using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace CrystalCast.Video;

/// <summary>
/// Gets WebView2 frames from Chromium's DevTools screencast. The page pushes a JPEG each time it repaints,
/// which is much faster than asking for a screenshot every frame (about 60 fps against 25 on Windows and
/// 10 under Wine). <see cref="KeepRunningAsync"/>, <see cref="PauseAsync"/> and the frame event run on the
/// browser thread. JPEG decoding runs on a worker thread, and a frame is acknowledged only once it was
/// decoded or skipped, so Chromium never encodes faster than the plugin can decode.
/// </summary>
internal sealed class WebView2ScreencastCapture : IDisposable
{
    private const int StallRestartMilliseconds = 5000;
    private const int MaxConsecutiveDecodeFailures = 30;

    private readonly Action<VideoFrame> publish;
    private readonly Func<long> nextSequence;
    private readonly Func<float> targetFps;
    private readonly object gate = new();
    private readonly AutoResetEvent workAvailable = new(false);
    private readonly Thread worker;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private SynchronizationContext? browserContext;
    private CoreWebView2? webView;
    private CoreWebView2DevToolsProtocolEventReceiver? receiver;
    private WebView2ScreencastSettings activeSettings;
    private (byte[] Jpeg, int SessionId)? pendingFrame;
    private volatile bool running;
    private volatile bool disposed;
    private long lastFrameReceivedMs;
    private readonly ScreencastFramePacer pacer = new();
    private int consecutiveDecodeFailures;
    private int restarts;
    private long framesSkipped;
    private volatile string lastDecodeError = string.Empty;
    private double lastDecodeMilliseconds;
    private int lastJpegBytes;
    private int lastWidth;
    private int lastHeight;

    public WebView2ScreencastCapture(Action<VideoFrame> publish, Func<long> nextSequence, Func<float> targetFps)
    {
        this.publish = publish;
        this.nextSequence = nextSequence;
        this.targetFps = targetFps;
        worker = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "CrystalCast WebView2 screencast decoder",
        };
        worker.Start();
    }

    /// <summary>Why screencast stopped for good; the caller then uses screenshots instead.</summary>
    public string FailureReason { get; private set; } = string.Empty;

    public bool IsRunning => running;

    public double LastDecodeMilliseconds => Volatile.Read(ref lastDecodeMilliseconds);

    public string Status
    {
        get
        {
            var size = lastWidth > 0
                ? $"{lastWidth}x{lastHeight}"
                : $"max {activeSettings.MaxWidth}x{activeSettings.MaxHeight}";
            var kilobytes = Volatile.Read(ref lastJpegBytes) / 1024.0;
            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"screencast {size} q{activeSettings.Quality} ({activeSettings.Reason}), {kilobytes:0} KB, {LastDecodeMilliseconds:0.#} ms decode, {Interlocked.Read(ref framesSkipped)} skipped");
            if (restarts > 0)
                text += $", restarted {restarts}x after quiet periods";
            var error = lastDecodeError;
            return string.IsNullOrEmpty(error) ? text : $"{text}, last decode error: {error}";
        }
    }

    /// <summary>
    /// Starts the screencast if needed and restarts it when it went silent. Returns false when screencast
    /// is off or failed, in which case <see cref="FailureReason"/> says why.
    /// </summary>
    public async Task<bool> KeepRunningAsync(CoreWebView2 currentWebView, WebView2ScreencastSettings settings)
    {
        if (disposed || !settings.Enabled || !string.IsNullOrEmpty(FailureReason))
        {
            if (running)
                await PauseAsync();
            if (!settings.Enabled && string.IsNullOrEmpty(FailureReason))
                FailureReason = $"screencast disabled by {settings.Reason}";
            return false;
        }

        if (Volatile.Read(ref consecutiveDecodeFailures) >= MaxConsecutiveDecodeFailures)
        {
            await PauseAsync();
            FailureReason = $"{MaxConsecutiveDecodeFailures} frames in a row could not be decoded: {lastDecodeError}";
            return false;
        }

        var sameTarget = ReferenceEquals(webView, currentWebView) && settings == activeSettings;
        if (running && sameTarget)
        {
            if (clock.ElapsedMilliseconds - Interlocked.Read(ref lastFrameReceivedMs) < StallRestartMilliseconds)
                return true;

            // A static page sends no frames, and a navigation can end a screencast quietly. Restarting is
            // cheap and always sends a fresh frame, so do it after a few silent seconds.
            restarts++;
        }

        try
        {
            if (running)
                await StopScreencastAsync();
            await StartScreencastAsync(currentWebView, settings);
            return true;
        }
        catch (Exception ex)
        {
            running = false;
            Unsubscribe();
            FailureReason = $"screencast could not start: {ex.GetBaseException().Message}";
            return false;
        }
    }

    /// <summary>Stops the screencast while capture is off, so the browser stops encoding frames.</summary>
    public async Task PauseAsync()
    {
        if (!running)
            return;

        try
        {
            await StopScreencastAsync();
        }
        catch
        {
            // The browser may already be gone; there is nothing left to stop then.
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        running = false;
        Unsubscribe();
        webView = null;
        workAvailable.Set();
        if (!worker.Join(2000))
            return; // A stuck decode keeps the event and decoder alive; the thread is a background thread.

        workAvailable.Dispose();
    }

    private async Task StartScreencastAsync(CoreWebView2 currentWebView, WebView2ScreencastSettings settings)
    {
        browserContext = SynchronizationContext.Current;
        if (!ReferenceEquals(webView, currentWebView))
        {
            Unsubscribe();
            webView = currentWebView;
        }

        activeSettings = settings;
        if (receiver == null)
        {
            receiver = currentWebView.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
            receiver.DevToolsProtocolEventReceived += OnScreencastFrame;
        }

        Interlocked.Exchange(ref lastFrameReceivedMs, clock.ElapsedMilliseconds);
        running = true;
        await currentWebView.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        await currentWebView.CallDevToolsProtocolMethodAsync(
            "Page.startScreencast",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"format\":\"jpeg\",\"quality\":{settings.Quality},\"maxWidth\":{settings.MaxWidth},\"maxHeight\":{settings.MaxHeight},\"everyNthFrame\":1}}"));
    }

    private async Task StopScreencastAsync()
    {
        running = false;
        var current = webView;
        if (current != null)
            await current.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}");
    }

    private void Unsubscribe()
    {
        if (receiver == null)
            return;

        try
        {
            receiver.DevToolsProtocolEventReceived -= OnScreencastFrame;
        }
        catch
        {
            // The WebView2 object may already be closed.
        }

        receiver = null;
    }

    private void OnScreencastFrame(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
            var root = document.RootElement;
            var sessionId = root.GetProperty("sessionId").GetInt32();
            var now = clock.ElapsedMilliseconds;
            Interlocked.Exchange(ref lastFrameReceivedMs, now);
            if (!running || disposed
                || !pacer.ShouldAccept(now, targetFps()))
            {
                Interlocked.Increment(ref framesSkipped);
                Acknowledge(sessionId);
                return;
            }

            var jpeg = root.GetProperty("data").GetBytesFromBase64();
            int? replacedSession = null;
            lock (gate)
            {
                replacedSession = pendingFrame?.SessionId;
                pendingFrame = (jpeg, sessionId);
            }

            if (replacedSession is { } replaced)
            {
                Interlocked.Increment(ref framesSkipped);
                Acknowledge(replaced);
            }

            workAvailable.Set();
        }
        catch (Exception ex)
        {
            lastDecodeError = $"bad screencast frame: {ex.Message}";
        }
    }

    private void Acknowledge(int sessionId)
    {
        var current = webView;
        if (current == null || disposed)
            return;

        _ = AcknowledgeAsync(current, sessionId);
    }

    private static async Task AcknowledgeAsync(CoreWebView2 current, int sessionId)
    {
        try
        {
            await current.CallDevToolsProtocolMethodAsync(
                "Page.screencastFrameAck",
                string.Create(CultureInfo.InvariantCulture, $"{{\"sessionId\":{sessionId}}}"));
        }
        catch
        {
            // A stopped screencast or closed browser no longer needs the acknowledgement.
        }
    }

    private void DecodeLoop()
    {
        // Any exception escaping this thread would take the game down with it, so nothing may leave.
        try
        {
            using var decoder = new WicBgraDecoder();
            while (!disposed)
            {
                workAvailable.WaitOne();
                (byte[] Jpeg, int SessionId)? next;
                lock (gate)
                {
                    next = pendingFrame;
                    pendingFrame = null;
                }

                if (next is { } frame && !disposed)
                    DecodeAndPublish(decoder, frame.Jpeg, frame.SessionId);
            }
        }
        catch (Exception ex)
        {
            lastDecodeError = $"decoder thread stopped: {ex.Message}";
            Volatile.Write(ref consecutiveDecodeFailures, MaxConsecutiveDecodeFailures);
        }
    }

    private void DecodeAndPublish(WicBgraDecoder decoder, byte[] jpeg, int sessionId)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var decoded = decoder.Decode(jpeg, nextSequence(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Volatile.Write(ref lastDecodeMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Volatile.Write(ref lastJpegBytes, jpeg.Length);
            lastWidth = decoded.Width;
            lastHeight = decoded.Height;
            Volatile.Write(ref consecutiveDecodeFailures, 0);
            try
            {
                publish(decoded);
            }
            catch
            {
                decoded.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref consecutiveDecodeFailures);
            lastDecodeError = ex.Message;
        }

        try
        {
            // The acknowledgement goes through the browser thread, which owns the WebView2 object.
            browserContext?.Post(_ => Acknowledge(sessionId), null);
        }
        catch
        {
            // The browser thread is shutting down; the screencast ends with it.
        }
    }
}
