using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrystalCast.Video;

internal interface IWebKitGtkHostListener
{
    void OnHostStatus(string status);
    void OnHostFailed(string reason);
    void OnHostGraphics(string browserTexture, string outputTexture);
    void OnHostFrame(VideoFrame frame, double extractMilliseconds);
    void OnPageLoading();
    void OnPageMessage(string json);
    void OnControlsVisibilityChanged(bool visible);
}

internal sealed record WebKitGtkHostOptions(
    string HelperPath,
    string LauncherPath,
    string LogPath,
    int Width,
    int Height,
    float FramesPerSecond);

/// <summary>
/// One native WebKitGTK helper process and its loopback control connection.
/// Frames arrive in three leased BGRA slots of a shared file; each is copied and acknowledged.
/// </summary>
internal sealed class WebKitGtkHostSession : IDisposable
{
    public const int Protocol = 1;
    private const int Slots = 3;
    private const int MaximumMessageBytes = 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly WebKitGtkHostOptions options;
    private readonly IWebKitGtkHostListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object writeLock = new();
    private readonly List<byte[]> pendingMessages = [];
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private NetworkStream? stream;
    private TcpClient? client;
    private int disposed;

    public WebKitGtkHostSession(WebKitGtkHostOptions options, IWebKitGtkHostListener listener)
    {
        this.options = options;
        this.listener = listener;
    }

    public string RuntimeDescription { get; private set; } = "not connected";

    public void Start()
    {
        _ = Task.Factory.StartNew(
            () => RunAsync(cancellation.Token),
            cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public void LoadPage(string url, string html) => Send(new { type = "load", url, html });

    public void PostToPage(string json)
    {
        using var document = JsonDocument.Parse(json);
        Send(new { type = "post", message = document.RootElement });
    }

    public void SetCaptureEnabled(bool enabled) => Send(new { type = "capture", enabled });

    public void SetMuted(bool muted) => Send(new { type = "muted", value = muted });

    public void SetFramesPerSecond(float fps) => Send(new { type = "fps", value = fps });

    public void SetControlsVisible(bool visible) => Send(new { type = "controls", visible });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            Send(new { type = "shutdown" });
        }
        catch
        {
            // Closing the connection also stops the helper.
        }

        cancellation.Cancel();
        lock (writeLock)
        {
            stream?.Dispose();
            client?.Dispose();
            stream = null;
            client = null;
        }
    }

    internal static byte[] Frame(object message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        var framed = new byte[json.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)json.Length);
        json.CopyTo(framed, 4);
        return framed;
    }

    private void Send(object message)
    {
        var framed = Frame(message);
        lock (writeLock)
        {
            if (Volatile.Read(ref disposed) != 0 && stream == null)
                return;

            if (stream == null)
            {
                pendingMessages.Add(framed);
                return;
            }

            try
            {
                stream.Write(framed);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // The read loop reports the lost connection.
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var listenerSocket = new TcpListener(IPAddress.Loopback, 0);
        MemoryMappedFile? mappedFile = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            listenerSocket.Start(1);
            var port = ((IPEndPoint)listenerSocket.LocalEndpoint).Port;
            StartHelper(port);
            listener.OnHostStatus("starting native WebKitGTK browser");

            TcpClient accepted;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    accepted = await listenerSocket.AcceptTcpClientAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new WebKitGtkHostException("the native browser did not start within 30 seconds. " + ReadLogTail());
                }
            }

            listenerSocket.Stop();
            accepted.NoDelay = true;
            var connection = accepted.GetStream();
            using var hello = await ReadMessageAsync(connection, cancellationToken)
                ?? throw new WebKitGtkHostException("the native browser closed before connecting. " + ReadLogTail());
            ValidateHello(hello.RootElement);

            lock (writeLock)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    accepted.Dispose();
                    return;
                }

                client = accepted;
                stream = connection;
                foreach (var message in pendingMessages)
                    stream.Write(message);
                pendingMessages.Clear();
            }

            var frameBytes = checked(options.Width * options.Height * 4);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var document = await ReadMessageAsync(connection, cancellationToken);
                if (document == null)
                    throw new WebKitGtkHostException("the native browser closed its connection. " + ReadLogTail());

                var root = document.RootElement;
                switch (GetString(root, "type"))
                {
                    case "ready":
                        (mappedFile, view) = OpenMapping(root, frameBytes);
                        Send(new { type = "mapped" });
                        RuntimeDescription = $"{RuntimeDescription}; {GetString(root, "renderer")} on {GetString(root, "display")}";
                        listener.OnHostStatus("native WebKitGTK browser running");
                        break;
                    case "frame":
                        if (view == null)
                            throw new WebKitGtkHostException("frame received before the shared frame file was ready");
                        DeliverFrame(root, view, frameBytes);
                        break;
                    case "page":
                        listener.OnPageMessage(GetString(root, "data"));
                        break;
                    case "loading":
                        listener.OnPageLoading();
                        break;
                    case "status":
                        listener.OnHostStatus(Bounded(GetString(root, "message")));
                        break;
                    case "graphics":
                        listener.OnHostGraphics(Bounded(GetString(root, "source")), Bounded(GetString(root, "output")));
                        break;
                    case "controls":
                        listener.OnControlsVisibilityChanged(root.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.True);
                        break;
                    case "fatal":
                        throw new WebKitGtkHostException(Bounded(GetString(root, "message")));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (Volatile.Read(ref disposed) == 0)
        {
            listener.OnHostFailed(ex is WebKitGtkHostException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Errors while shutting down are expected.
        }
        finally
        {
            listenerSocket.Stop();
            lock (writeLock)
            {
                stream?.Dispose();
                client?.Dispose();
                stream = null;
                client = null;
            }

            view?.Dispose();
            mappedFile?.Dispose();
        }
    }

    private void StartHelper(int port)
    {
        var helper = WineHostInterop.ToUnixPath(options.HelperPath)
            ?? throw new WebKitGtkHostException($"Wine cannot map {options.HelperPath} to a Linux path");
        var launcher = WineHostInterop.ToUnixPath(options.LauncherPath)
            ?? throw new WebKitGtkHostException($"Wine cannot map {options.LauncherPath} to a Linux path");
        var log = WineHostInterop.ToUnixPath(options.LogPath)
            ?? throw new WebKitGtkHostException($"Wine cannot map {options.LogPath} to a Linux path");
        var fps = (int)Math.Round(Math.Clamp(options.FramesPerSecond, 1.0f, 120.0f));
        WineHostInterop.StartUnixProcess(
            "/bin/sh",
            [launcher, helper, "--width", options.Width.ToString(), "--height", options.Height.ToString(), "--fps", fps.ToString()],
            new Dictionary<string, string>
            {
                ["CRYSTALCAST_HOST_PORT"] = port.ToString(),
                ["CRYSTALCAST_HOST_TOKEN"] = token,
                ["CRYSTALCAST_HOST_LOG"] = log,
            });
    }

    private void ValidateHello(JsonElement hello)
    {
        var presented = Encoding.ASCII.GetBytes(GetString(hello, "token"));
        if (GetString(hello, "type") != "hello"
            || !CryptographicOperations.FixedTimeEquals(presented, Encoding.ASCII.GetBytes(token)))
        {
            throw new WebKitGtkHostException("rejected an unauthenticated native browser connection");
        }

        if (!hello.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out var version) || version != Protocol)
            throw new WebKitGtkHostException("the native browser helper does not match this plugin version");

        RuntimeDescription = $"GTK {Bounded(GetString(hello, "gtk"))}, WebKitGTK {Bounded(GetString(hello, "webkit"))}";
    }

    private (MemoryMappedFile File, MemoryMappedViewAccessor View) OpenMapping(JsonElement ready, int frameBytes)
    {
        if (!ready.TryGetProperty("width", out var width) || width.GetInt32() != options.Width
            || !ready.TryGetProperty("height", out var height) || height.GetInt32() != options.Height
            || !ready.TryGetProperty("slots", out var slots) || slots.GetInt32() != Slots)
        {
            throw new WebKitGtkHostException("the native browser reported an unexpected frame layout");
        }

        var unixPath = GetString(ready, "mapping");
        var windowsPath = WineHostInterop.ToWindowsPath(unixPath)
            ?? throw new WebKitGtkHostException($"Wine cannot open the shared frame file {unixPath}");
        var file = new FileStream(windowsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (file.Length < (long)frameBytes * Slots)
                throw new WebKitGtkHostException("the shared frame file is too small");

            var mapped = MemoryMappedFile.CreateFromFile(file, null, (long)frameBytes * Slots, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            return (mapped, mapped.CreateViewAccessor(0, (long)frameBytes * Slots, MemoryMappedFileAccess.Read));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private unsafe void DeliverFrame(JsonElement root, MemoryMappedViewAccessor view, int frameBytes)
    {
        if (!root.TryGetProperty("seq", out var seqProperty) || !seqProperty.TryGetInt64(out var sequence)
            || !root.TryGetProperty("slot", out var slotProperty) || !slotProperty.TryGetInt32(out var slot)
            || slot < 0 || slot >= Slots)
        {
            throw new WebKitGtkHostException("the native browser sent an invalid frame notice");
        }

        var extractMilliseconds = root.TryGetProperty("extract_us", out var extract) && extract.TryGetDouble(out var us)
            ? us / 1000.0
            : 0.0;
        var frame = VideoFrame.Rent(options.Width, options.Height, sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        byte* pointer = null;
        var handle = view.SafeMemoryMappedViewHandle;
        try
        {
            handle.AcquirePointer(ref pointer);
            new ReadOnlySpan<byte>(pointer + view.PointerOffset + ((long)slot * frameBytes), frameBytes)
                .CopyTo(frame.Pixels.AsSpan(0, frame.PixelLength));
        }
        catch
        {
            frame.Dispose();
            throw;
        }
        finally
        {
            if (pointer != null)
                handle.ReleasePointer();
        }

        Send(new { type = "ack", seq = sequence });
        listener.OnHostFrame(frame, extractMilliseconds);
    }

    private static async Task<JsonDocument?> ReadMessageAsync(Stream connection, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(connection, header, cancellationToken))
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaximumMessageBytes)
            throw new WebKitGtkHostException("the native browser sent an oversized message");

        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(connection, body, cancellationToken))
            return null;

        return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream connection, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await connection.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                return false;
            offset += read;
        }

        return true;
    }

    private string ReadLogTail()
    {
        try
        {
            if (!File.Exists(options.LogPath))
                return "No native browser log was written; Wine may not be able to start Linux programs.";

            var lines = File.ReadAllLines(options.LogPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(3)
                .Select(line => Bounded(line));
            var text = string.Join(" | ", lines);
            return text.Length == 0 ? "The native browser log is empty." : $"Native browser log: {text}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string Bounded(string value) => value.Length <= 300 ? value : value[..300];
}

internal sealed class WebKitGtkHostException(string message) : Exception(message);
