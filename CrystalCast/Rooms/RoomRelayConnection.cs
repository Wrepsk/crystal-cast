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
    /// <summary>Matches the relay's limit for what we send.</summary>
    private const int MaxMessageBytes = 32 * 1024;

    /// <summary>What we accept: a persistent room's member list can be larger than a state.</summary>
    private const int MaxReceiveBytes = 64 * 1024;
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    private readonly Uri relayBaseUri;
    private readonly string code;
    private readonly string? hostKey;
    private readonly string? memberKey;
    private volatile string passwordHash = string.Empty;
    private volatile string name;
    private readonly ConcurrentQueue<RoomRelayMessage> inbox = new();
    private readonly ConcurrentQueue<string> outbox = new();
    private readonly SemaphoreSlim outboxSignal = new(0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private volatile RoomConnectionStatus status = RoomConnectionStatus.Connecting;
    private volatile string lastError = string.Empty;

    public RoomRelayConnection(Uri relayBaseUri, string code, string? hostKey, string name)
        : this(relayBaseUri, code, hostKey, memberKey: null, passwordHash: string.Empty, name)
    {
    }

    private RoomRelayConnection(Uri relayBaseUri, string code, string? hostKey, string? memberKey, string passwordHash, string name)
    {
        this.relayBaseUri = relayBaseUri;
        this.code = code;
        this.hostKey = hostKey;
        this.memberKey = memberKey;
        this.passwordHash = passwordHash;
        this.name = RoomUsername.Display(name);
        loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// A connection to a persistent room as the member <paramref name="memberKey"/>. The password is only
    /// needed until the relay has let this member in once; <see cref="ForgetPassword"/> drops it then.
    /// </summary>
    public static RoomRelayConnection ForPersistentRoom(Uri relayBaseUri, string code, string memberKey, string name, string password) =>
        new(relayBaseUri, code, hostKey: null, memberKey, RoomMemberKeys.HashPassword(password), name);

    /// <summary>The relay knows this member now; reconnects don't need the password.</summary>
    public void ForgetPassword() => passwordHash = string.Empty;

    /// <summary>
    /// Changes the name others in the room see. Sent now when connected, and used for every reconnect after.
    /// </summary>
    public void Rename(string newName)
    {
        var display = RoomUsername.Display(newName);
        if (string.Equals(display, name, StringComparison.Ordinal))
            return;

        name = display;
        Send(new { type = "name", name = display });
    }

    public RoomConnectionStatus Status => status;
    public string LastError => lastError;

    /// <summary>True when the relay refused the room (closed, unknown code, wrong key); retrying won't help.</summary>
    public bool Refused { get; private set; }

    /// <summary>
    /// Why a persistent room refused or dropped this connection, when the relay said: "gone", "banned",
    /// "password", "full", "removed". Empty otherwise.
    /// </summary>
    public string RefusalReason { get; private set; } = string.Empty;

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

    internal static Uri BuildPersistentSocketUri(Uri relayBaseUri, string code, string memberKey, string name, string passwordHash)
    {
        var scheme = relayBaseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        var query = $"key={Uri.EscapeDataString(memberKey)}&name={Uri.EscapeDataString(RoomUsername.Display(name))}";
        if (passwordHash.Length > 0)
            query += $"&password={Uri.EscapeDataString(passwordHash)}";

        var builder = new UriBuilder(relayBaseUri)
        {
            Scheme = scheme,
            Path = $"{relayBaseUri.AbsolutePath.TrimEnd('/')}/persistent/{code}/socket",
            Query = query,
        };
        if (builder.Port == 80 && scheme == "ws" || builder.Port == 443 && scheme == "wss")
            builder.Port = -1;

        return builder.Uri;
    }

    internal static Uri BuildSocketUri(Uri relayBaseUri, string code, string? hostKey, string name)
    {
        var scheme = relayBaseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        var role = hostKey == null ? "role=viewer" : $"role=host&key={Uri.EscapeDataString(hostKey)}";
        var builder = new UriBuilder(relayBaseUri)
        {
            Scheme = scheme,
            Path = $"{relayBaseUri.AbsolutePath.TrimEnd('/')}/rooms/{code}/socket",
            Query = $"{role}&name={Uri.EscapeDataString(RoomUsername.Display(name))}",
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
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                // Built for each attempt, so a reconnect after a rename uses the new name.
                var uri = memberKey != null
                    ? BuildPersistentSocketUri(relayBaseUri, code, memberKey, name, passwordHash)
                    : BuildSocketUri(relayBaseUri, code, hostKey, name);
                await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
                status = RoomConnectionStatus.Connected;
                lastError = string.Empty;
                attempt = 0;
                await PumpAsync(socket).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex) when (IsRefusal(ex, socket))
            {
                RefusalReason = ReadRefusalReason(socket);
                lastError = DescribeRefusal(RefusalReason);
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
        var buffer = new byte[MaxReceiveBytes];
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
                // 4001: the room closed; it will not come back. 4002/4003 (persistent rooms): removed or banned.
                if (result.CloseStatus is { } closeStatus && (int)closeStatus is 4001 or 4002 or 4003)
                {
                    RefusalReason = (int)closeStatus switch { 4001 => "gone", 4002 => "removed", _ => "banned" };
                    lastError = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                        ? DescribeRefusal(RefusalReason)
                        : result.CloseStatusDescription;
                    Refused = true;
                }

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
    private static bool IsRefusal(WebSocketException ex, ClientWebSocket socket)
    {
        // A full room (503) or a relay hiccup may pass; keep retrying those.
        if ((int)socket.HttpStatusCode >= 500)
            return false;

        return ex.WebSocketErrorCode == WebSocketError.NotAWebSocket
            || ex.GetBaseException() is HttpRequestException { StatusCode: { } code } && (int)code is 400 or 403 or 404;
    }

    /// <summary>A persistent room says why it refused in a header; the body of a failed upgrade can't be read.</summary>
    private static string ReadRefusalReason(ClientWebSocket socket)
    {
        if (socket.HttpResponseHeaders != null
            && socket.HttpResponseHeaders.TryGetValue("X-CrystalCast-Refusal", out var values))
        {
            return values.FirstOrDefault() ?? string.Empty;
        }

        return (int)socket.HttpStatusCode == 404 ? "gone" : string.Empty;
    }

    internal static string DescribeRefusal(string reason) => reason switch
    {
        "gone" => "That room doesn't exist or was deleted.",
        "banned" => "You're banned from this room.",
        "password" => "Wrong password.",
        "full" => "This room is full.",
        "removed" => "You were removed from this room.",
        _ => "The relay refused this room.",
    };
}
