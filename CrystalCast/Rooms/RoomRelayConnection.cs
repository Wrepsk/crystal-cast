using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CrystalCast.Ipc;

namespace CrystalCast.Rooms;

internal enum RoomConnectionStatus
{
    Connecting,
    Connected,
    Reconnecting,
    Closed,
}

/// <summary>
/// One WebSocket to the relay for one room, as host or viewer. Runs on a background task and reconnects
/// with growing delays. Messages are queued for the game's main thread, which reads them with
/// <see cref="TryDequeue"/>; nothing here touches game state.
/// </summary>
internal sealed class RoomRelayConnection : IDisposable
{
    /// <summary>Matches the relay's limit.</summary>
    private const int MaxMessageBytes = 32 * 1024;
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    private readonly Uri socketUri;
    private readonly ConcurrentQueue<RoomRelayMessage> inbox = new();
    private readonly ConcurrentQueue<string> outbox = new();
    private readonly SemaphoreSlim outboxSignal = new(0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private volatile RoomConnectionStatus status = RoomConnectionStatus.Connecting;
    private volatile string lastError = string.Empty;

    public RoomRelayConnection(Uri relayBaseUri, string code, string? hostKey)
    {
        socketUri = BuildSocketUri(relayBaseUri, code, hostKey);
        loop = Task.Run(RunAsync);
    }

    public RoomConnectionStatus Status => status;
    public string LastError => lastError;

    /// <summary>True when the relay refused the room (closed, unknown code, wrong key); retrying won't help.</summary>
    public bool Refused { get; private set; }

    public bool TryDequeue(out RoomRelayMessage message) => inbox.TryDequeue(out message!);

    public void Send(object message)
    {
        if (status == RoomConnectionStatus.Closed)
            return;

        outbox.Enqueue(IpcJsonService.Serialize(message));
        outboxSignal.Release();
    }

    public void Dispose()
    {
        status = RoomConnectionStatus.Closed;
        stop.Cancel();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down; the task ends on cancellation.
        }

        stop.Dispose();
        outboxSignal.Dispose();
    }

    internal static Uri BuildSocketUri(Uri relayBaseUri, string code, string? hostKey)
    {
        var scheme = relayBaseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        var builder = new UriBuilder(relayBaseUri)
        {
            Scheme = scheme,
            Path = $"{relayBaseUri.AbsolutePath.TrimEnd('/')}/rooms/{code}/socket",
            Query = hostKey == null ? "role=viewer" : $"role=host&key={Uri.EscapeDataString(hostKey)}",
        };
        if (builder.Port == 80 && scheme == "ws" || builder.Port == 443 && scheme == "wss")
            builder.Port = -1;

        return builder.Uri;
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(socketUri, connectTimeout.Token).ConfigureAwait(false);
                status = RoomConnectionStatus.Connected;
                lastError = string.Empty;
                attempt = 0;
                await PumpAsync(socket).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex) when (IsRefusal(ex))
            {
                lastError = "The relay refused this room.";
                Refused = true;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.GetBaseException().Message;
            }

            if (stop.IsCancellationRequested || Refused)
                break;

            status = RoomConnectionStatus.Reconnecting;
            var delay = ReconnectDelays[Math.Min(attempt++, ReconnectDelays.Length - 1)];
            try
            {
                await Task.Delay(delay, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        status = RoomConnectionStatus.Closed;
    }

    private async Task PumpAsync(ClientWebSocket socket)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var receive = ReceiveAsync(socket, linked.Token);
        var send = SendAsync(socket, linked.Token);
        await Task.WhenAny(receive, send).ConfigureAwait(false);
        linked.Cancel();
        try
        {
            await Task.WhenAll(receive, send).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // The other side is gone already.
            }
        }

        // Rethrow a receive failure so the reconnect loop records it.
        if (receive.IsFaulted)
            await receive.ConfigureAwait(false);
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[MaxMessageBytes];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var length = 0;
            WebSocketReceiveResult result;
            do
            {
                if (length >= buffer.Length)
                    throw new InvalidOperationException("The relay sent a message that is too large.");

                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), token).ConfigureAwait(false);
                length += result.Count;
            }
            while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // 4001: the room closed; it will not come back.
                if (result.CloseStatus is { } closeStatus && (int)closeStatus == 4001)
                    Refused = true;
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            try
            {
                var message = IpcJsonService.Deserialize<RoomRelayMessage>(Encoding.UTF8.GetString(buffer, 0, length));
                if (message != null)
                    inbox.Enqueue(message);
            }
            catch (JsonException)
            {
                // Not something this version understands; skip it.
            }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await outboxSignal.WaitAsync(token).ConfigureAwait(false);
            while (outbox.TryDequeue(out var text))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > MaxMessageBytes)
                    continue;

                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The relay answers a refused room with an HTTP error instead of upgrading the connection.</summary>
    private static bool IsRefusal(WebSocketException ex)
    {
        return ex.WebSocketErrorCode == WebSocketError.NotAWebSocket
            || ex.GetBaseException() is HttpRequestException { StatusCode: { } code } && (int)code is 400 or 403 or 404;
    }
}
