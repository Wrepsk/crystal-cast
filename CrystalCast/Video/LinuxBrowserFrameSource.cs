using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace CrystalCast.Video;

// Media/provider policy stays in CrystalCast; the native helper only hosts the
// web engine and exports its completed compositor buffers. No Dalamud calls or
// synchronous socket/process waits occur on the draw thread.
internal sealed class LinuxBrowserFrameSource : IVideoFrameSource, IMediaPlaybackController,
    IMediaPlaybackTelemetrySource, IBrowserFrameSourceRuntime, IBrowserControlsHost
{
    private readonly object gate = new();
    private readonly BrowserFrameSourceRequest request;
    private readonly ILinuxBrowserLauncher launcher;
    private readonly bool preferSharedFrames;
    private readonly BrowserSourceDescriptor descriptor;
    private readonly IBrowserSourceReference? source;
    private readonly BrowserPlayerPageResource? page;
    private readonly string nonce;
    private readonly BrowserPlaybackIntent playbackIntent;
    private CancellationTokenSource? lifetime;
    private Task? worker;
    private Channel<string>? outgoing;
    private VideoFrame? latestFrame;
    private MediaPlaybackTelemetry telemetry = new();
    private string nativeStatus = "native browser not started", playerStatus = "", metricsStatus = "";
    private long retryAfter, publishedSequence, receivedFrames, transferTicks;
    private bool disposed, captureRequested, playerReady;
    private bool audioEnabled, loop, playlistAutoplayNext;
    private float volume, playbackRate, captureFps, detectedVideoFps;

    public LinuxBrowserFrameSource(BrowserFrameSourceRequest request, ILinuxBrowserLauncher? launcher = null,
        bool preferSharedFrames = true)
    {
        this.request = request;
        this.launcher = launcher ?? new LinuxBrowserLauncher();
        this.preferSharedFrames = preferSharedFrames;
        descriptor = request.Descriptor ?? BrowserSourceDescriptors.Get(request.ProviderKind);
        playbackIntent = new BrowserPlaybackIntent(request.Autoplay);
        audioEnabled = request.AudioEnabled;
        volume = Math.Clamp(request.Volume, 0, 1);
        playbackRate = Math.Clamp(request.PlaybackRate, .25f, 2);
        loop = request.Loop;
        playlistAutoplayNext = request.PlaylistAutoplayNext;
        captureFps = Math.Clamp(request.CaptureFps, 1, 120);
        if (descriptor.TryParse(request.Input, out var parsed)
            && (request.ProviderKind != BrowserSourceProviderKind.GenericWeb || request.IsNavigationAllowed?.Invoke(request.Input) != false))
        {
            source = parsed;
            if (request.ProviderKind != BrowserSourceProviderKind.GenericWeb)
                page = BrowserPlayerPageResource.Create(descriptor, parsed, new BrowserPlaybackSettings(
                    request.Autoplay, loop, playlistAutoplayNext, audioEnabled, volume, playbackRate));
        }
        nonce = page?.Nonce ?? BrowserPageMessaging.CreateNonce();
    }

    public string Name => $"{descriptor.DisplayName} native Linux browser (WPE WebKit, experimental)";
    public int Width => Math.Clamp(request.Width, 320, LinuxBrowserProtocol.MaximumWidth);
    public int Height => Math.Clamp(request.Height, 180, LinuxBrowserProtocol.MaximumHeight);
    public float FramesPerSecond => Volatile.Read(ref captureFps);
    public bool IsRunning { get { lock (gate) return worker is { IsCompleted: false } && captureRequested; } }
    public string Status => $"{nativeStatus}; {playerStatus}; {metricsStatus}";
    public BrowserSourceProviderKind ProviderKind => request.ProviderKind;
    public float DetectedVideoFps => Volatile.Read(ref detectedVideoFps);
    // Interactive native window/input forwarding is not part of the first slice.
    public bool BrowserControlsAvailable => false;
    public bool BrowserControlsVisible => false;
    public bool ShowBrowserControls() => false;
    public bool HideBrowserControls() => false;

    public void Start()
    {
        lock (gate)
        {
            if (disposed) return;
            if (source == null) { nativeStatus = descriptor.InvalidSourceMessage; return; }
            if (!captureRequested)
            {
                captureRequested = true;
                Queue(new { type = "capture", enabled = true });
                if (playbackIntent.IsPlayRequested) QueuePlayback(BrowserPageMessaging.Play(nonce));
            }
            if (worker is { IsCompleted: false } || Environment.TickCount64 < retryAfter) return;
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
            {
                SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest,
            });
            playerReady = false;
            worker = Task.Run(() => RunAsync(lifetime.Token));
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!captureRequested) return;
            captureRequested = false;
            Queue(new { type = "capture", enabled = false });
            QueuePlayback(BrowserPageMessaging.Pause(nonce));
        }
    }

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        var current = Volatile.Read(ref latestFrame);
        if (current != null) return current.TryAcquire(out frame);
        frame = null!;
        return false;
    }
    public bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry current)
    {
        current = Volatile.Read(ref telemetry);
        return source != null;
    }
    public void Play() { playbackIntent.RequestPlay(); Start(); QueuePlayback(BrowserPageMessaging.Play(nonce)); }
    public void Pause()
    {
        playbackIntent.RequestPause();
        lock (gate)
        {
            captureRequested = false;
            Queue(new { type = "capture", enabled = false });
            QueuePlayback(BrowserPageMessaging.Pause(nonce));
        }
    }
    public void Restart() { playbackIntent.RequestPlay(); Start(); QueuePlayback(BrowserPageMessaging.Restart(nonce)); }
    public void SeekBy(double seconds) { if (double.IsFinite(seconds)) QueuePlayback(BrowserPageMessaging.SeekBy(nonce, seconds)); }
    public void SeekTo(double seconds) { if (double.IsFinite(seconds)) QueuePlayback(BrowserPageMessaging.SeekTo(nonce, seconds)); }
    public void UpdateCaptureFps(float fps)
    {
        if (!float.IsFinite(fps)) return;
        var next = Math.Clamp(fps, 1, 120);
        if (Math.Abs(next - FramesPerSecond) < .01f) return;
        Volatile.Write(ref captureFps, next);
        Queue(new { type = "fps", fps = next });
    }
    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        lock (gate)
        {
            volume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0;
            playbackRate = float.IsFinite(playbackRate) ? Math.Clamp(playbackRate, .25f, 2) : 1;
            if (this.audioEnabled == audioEnabled && Math.Abs(this.volume - volume) < .0001f
                && Math.Abs(this.playbackRate - playbackRate) < .0001f && this.loop == loop
                && this.playlistAutoplayNext == playlistAutoplayNext) return;
            this.audioEnabled = audioEnabled;
            this.volume = volume;
            this.playbackRate = playbackRate;
            this.loop = loop;
            this.playlistAutoplayNext = playlistAutoplayNext;
            QueueSettings();
        }
    }
    private void QueueSettings() => QueuePlayback(BrowserPageMessaging.Settings(nonce, audioEnabled, volume, playbackRate, loop, playlistAutoplayNext));
    private void QueuePlayback(string message)
    {
        lock (gate) { if (playerReady) Queue(new { type = "command", message }); }
    }
    private void Queue<T>(T value) => outgoing?.Writer.TryWrite(JsonSerializer.Serialize(value));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        LinuxBrowserEndpoint? endpoint = null;
        LinuxBrowserFrameMapping? mapping = null;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var client = new TcpClient { NoDelay = true };
        using var sending = new SemaphoreSlim(1);
        Task? sender = null, heartbeat = null;
        try
        {
            nativeStatus = "starting native Linux browser";
            endpoint = await launcher.LaunchAsync(connection.Token).ConfigureAwait(false);
            if (preferSharedFrames)
            {
                try { mapping = new LinuxBrowserFrameMapping(Path.Combine(endpoint.Directory, "frames.bin")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                { nativeStatus = "shared frames unavailable; using uncompressed socket transfer"; }
            }
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, connection.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), connection.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            async Task Send(string json)
            {
                await sending.WaitAsync(connection.Token).ConfigureAwait(false);
                try
                {
                    await LinuxBrowserProtocol.WriteJsonAsync(stream, json, connection.Token)
                        .WaitAsync(TimeSpan.FromSeconds(5), connection.Token).ConfigureAwait(false);
                }
                finally { sending.Release(); }
            }
            await Send(JsonSerializer.Serialize(new { type = "hello", version = 1, token = endpoint.Token,
                transport = mapping == null ? "tcp" : "shared-file" })).ConfigureAwait(false);
            var helloHeader = await LinuxBrowserProtocol.ReadHeaderAsync(stream, connection.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), connection.Token).ConfigureAwait(false);
            if (helloHeader.Kind != 1) throw new InvalidDataException("Native browser did not send its handshake.");
            using (var hello = await LinuxBrowserProtocol.ReadJsonAsync(stream, helloHeader.Length, connection.Token).ConfigureAwait(false))
            {
                var root = hello.RootElement;
                if (root.GetProperty("type").GetString() != "hello" || root.GetProperty("version").GetInt32() != 1
                    || root.GetProperty("transport").GetString() != (mapping == null ? "tcp" : "shared-file")
                    || root.GetProperty("slotSize").GetInt32() != LinuxBrowserProtocol.SlotSize
                    || root.GetProperty("slots").GetInt32() != LinuxBrowserProtocol.SlotCount)
                    throw new InvalidDataException("Native browser handshake is incompatible.");
            }
            nativeStatus = "connected; " + (mapping == null ? "uncompressed BGRA socket frames" : "leased shared-memory BGRA frames");
            await Send(BuildLoadMessage()).ConfigureAwait(false);
            lock (gate) Queue(new { type = "capture", enabled = captureRequested });
            sender = Task.Run(async () =>
            {
                try
                {
                    await foreach (var command in outgoing!.Reader.ReadAllAsync(connection.Token).ConfigureAwait(false))
                        await Send(command).ConfigureAwait(false);
                }
                catch { connection.Cancel(); throw; }
            }, connection.Token);
            heartbeat = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                    while (await timer.WaitForNextTickAsync(connection.Token).ConfigureAwait(false))
                        await Send("{\"type\":\"ping\"}").ConfigureAwait(false);
                }
                catch { connection.Cancel(); throw; }
            }, connection.Token);
            var sequenceBase = Interlocked.Read(ref publishedSequence);
            while (!connection.IsCancellationRequested)
            {
                var header = await LinuxBrowserProtocol.ReadHeaderAsync(stream, connection.Token)
                    .WaitAsync(TimeSpan.FromSeconds(15), connection.Token).ConfigureAwait(false);
                var started = Stopwatch.GetTimestamp();
                if (header.Kind == 2)
                {
                    var frame = await LinuxBrowserProtocol.ReadFrameAsync(stream, header.Length, connection.Token, sequenceBase).ConfigureAwait(false);
                    Publish(frame, started);
                }
                else
                {
                    using var message = await LinuxBrowserProtocol.ReadJsonAsync(stream, header.Length, connection.Token).ConfigureAwait(false);
                    var root = message.RootElement;
                    if (root.GetProperty("type").GetString() == "frame")
                    {
                        if (mapping == null) throw new InvalidDataException("Unexpected shared frame on a socket-only connection.");
                        var frame = mapping.Read(root, sequenceBase);
                        try { await Send(JsonSerializer.Serialize(new { type = "ack", slot = root.GetProperty("slot").GetInt32(), sequence = root.GetProperty("sequence").GetInt64() })).ConfigureAwait(false); }
                        catch { frame.Dispose(); throw; }
                        Publish(frame, started);
                    }
                    else ReceiveMessage(root);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            nativeStatus = "native browser failed: " + BrowserMessageValidator.BoundText(ex.GetBaseException().Message, "unknown failure");
        }
        finally
        {
            connection.Cancel();
            client.Close(); // EOF tells the helper to stop and reap its browser children.
            if (sender != null) { try { await sender.ConfigureAwait(false); } catch { } }
            if (heartbeat != null) { try { await heartbeat.ConfigureAwait(false); } catch { } }
            mapping?.Dispose();
            endpoint?.Launcher?.Dispose();
            lock (gate) { playerReady = false; retryAfter = Environment.TickCount64 + 10000; }
            Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        }
    }

    internal string BuildLoadMessage() => JsonSerializer.Serialize(new
    {
        type = "load", uri = page?.Url ?? ((GenericWebSourceReference)source!).Url,
        html = page?.Html ?? "", script = page == null ? GenericWebBrowserFrameSource.BuildControllerScript(nonce) : "",
        generic = page == null, lockNavigation = request.IsNavigationAllowed != null,
        width = Width, height = Height, fps = FramesPerSecond,
    });

    private void Publish(VideoFrame frame, long started)
    {
        lock (gate)
        {
            if (disposed || frame.Sequence <= publishedSequence) { frame.Dispose(); return; }
            publishedSequence = frame.Sequence;
            Interlocked.Exchange(ref latestFrame, frame)?.Dispose();
        }
        Interlocked.Increment(ref receivedFrames);
        Interlocked.Add(ref transferTicks, Stopwatch.GetTimestamp() - started);
    }

    private void ReceiveMessage(JsonElement root)
    {
        var type = root.GetProperty("type").GetString();
        if (type == "message")
        {
            if (!BrowserMessageValidator.TryParseAuthenticated(root.GetProperty("message").GetString(), nonce, out var document, out _)) return;
            using (document)
            {
                var message = document!.RootElement;
                switch (message.GetProperty("type").GetString())
                {
                    case "ready":
                        lock (gate)
                        {
                            playerReady = true;
                            QueueSettings();
                            QueuePlayback(captureRequested && playbackIntent.IsPlayRequested
                                ? BrowserPageMessaging.Play(nonce) : BrowserPageMessaging.Pause(nonce));
                        }
                        playerStatus = "player/controller ready";
                        break;
                    case "status": UpdateTelemetry(message); break;
                    case "video-fps":
                        var fps = (float)BrowserSourceDescriptors.TryGetDouble(message, "fps", 0);
                        if (float.IsFinite(fps) && fps is >= 1 and <= 240) Volatile.Write(ref detectedVideoFps, fps);
                        break;
                    case "error": playerStatus = descriptor.DescribeError(message); break;
                    case "script-error": playerStatus = BrowserSourceDescriptors.TryGetString(message, "message", "script error"); break;
                }
            }
        }
        else if (type == "metrics")
        {
            var count = Interlocked.Read(ref receivedFrames);
            var averageMs = count == 0 ? 0 : Interlocked.Read(ref transferTicks) * 1000.0 / Stopwatch.Frequency / count;
            metricsStatus = $"received {count}; read/copy {averageMs:0.000} ms/frame; helper dropped {root.GetProperty("dropped").GetInt64()}";
        }
        else if (type is "engine" or "buffer") metricsStatus = root.GetProperty("message").GetString() ?? "";
        else if (type is "error" or "navigation-blocked") playerStatus = BrowserMessageValidator.BoundText(root.GetProperty("message").GetString(), "native browser error");
    }

    private void UpdateTelemetry(JsonElement root)
    {
        var state = BrowserSourceDescriptors.TryGetInt(root, "state", 0);
        var title = BrowserSourceDescriptors.TryGetString(root, "title", "");
        var id = BrowserSourceDescriptors.TryGetString(root, "videoId", source!.VideoId);
        if (!descriptor.IsValidVideoId(id)) id = source!.VideoId;
        var canonical = descriptor.BuildCanonicalSourceUrl(source!, id);
        if (page == null && GenericWebUrl.TryParseSource(BrowserSourceDescriptors.TryGetString(root, "url", canonical, BrowserUriPolicy.MaximumUrlLength), out var generic))
            canonical = generic.Url;
        var rate = (float)BrowserSourceDescriptors.TryGetDouble(root, "rate", playbackRate);
        Volatile.Write(ref telemetry, new MediaPlaybackTelemetry
        {
            State = state switch { 1 or 3 => ScreenPlaybackState.Playing, 2 or 5 => ScreenPlaybackState.Paused, _ => ScreenPlaybackState.Stopped },
            PositionMs = BrowserMessageValidator.ToBoundedMilliseconds(BrowserSourceDescriptors.TryGetDouble(root, "positionSeconds", 0)),
            DurationMs = BrowserMessageValidator.ToBoundedMilliseconds(BrowserSourceDescriptors.TryGetDouble(root, "durationSeconds", 0)),
            Rate = float.IsFinite(rate) ? Math.Clamp(rate, .25f, 2) : 1,
            Title = title, VideoId = id, CanonicalUrl = canonical,
            HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DetectedVideoFps = DetectedVideoFps,
        });
        playerStatus = $"player state {state}; {telemetry.PositionMs / 1000.0:0.0}s / {telemetry.DurationMs / 1000.0:0.0}s";
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            captureRequested = false;
            outgoing?.Writer.TryComplete();
            lifetime?.Cancel();
            // The worker owns its cancellation source until network tasks exit.
            if (worker != null) _ = worker.ContinueWith(_ => lifetime?.Dispose(), TaskScheduler.Default);
            else lifetime?.Dispose();
            Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        }
    }
}
