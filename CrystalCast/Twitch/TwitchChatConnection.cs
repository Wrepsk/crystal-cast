using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace CrystalCast.Twitch;

internal enum TwitchChatStatus
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>
/// A read-only connection to Twitch chat, without an account. Twitch lets anyone read chat as an anonymous
/// "justinfan" user. Runs on a background task; parsed lines are queued for the game's main thread.
/// <para>
/// The connection opens only while the tab asks for a channel (<see cref="Want"/>), and closes on its own a
/// minute after the tab stops asking, so a closed tab costs nothing.
/// </para>
/// </summary>
internal sealed class TwitchChatConnection : IDisposable
{
    private static readonly Uri ChatUri = new("wss://irc-ws.chat.twitch.tv:443");
    private const int MaxQueuedLines = 2000;
    private const long IdleCloseMs = 60_000;
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    private readonly ConcurrentQueue<TwitchIrcMessage> inbox = new();
    private readonly ConcurrentQueue<string> outbox = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private volatile string wantedChannel = string.Empty;
    private long lastWantedTick;
    private volatile TwitchChatStatus status = TwitchChatStatus.Idle;
    private volatile string lastError = string.Empty;
    private long connectionGeneration;

    public TwitchChatConnection()
    {
        loop = Task.Run(RunAsync);
    }

    public TwitchChatStatus Status => status;
    public string LastError => lastError;

    /// <summary>Bumped on every new connection, so the tab knows to re-show "joining".</summary>
    public long ConnectionGeneration => Interlocked.Read(ref connectionGeneration);

    /// <summary>Called every frame the tab shows a channel's chat. Joins it, leaving the previous one.</summary>
    public void Want(string channel)
    {
        Interlocked.Exchange(ref lastWantedTick, Environment.TickCount64);
        channel = channel.ToLowerInvariant();
        if (wantedChannel == channel && status != TwitchChatStatus.Idle)
            return;

        wantedChannel = channel;
        // One pending wake is enough; asking every frame must not pile them up.
        if (signal.CurrentCount == 0)
            Wake();
    }

    public bool TryDequeue(out TwitchIrcMessage message) => inbox.TryDequeue(out message!);

    public void Dispose()
    {
        stop.Cancel();
        Wake();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down; the task ends on cancellation.
        }

        stop.Dispose();
        signal.Dispose();
    }

    private bool IsWanted =>
        !string.IsNullOrEmpty(wantedChannel)
        && Environment.TickCount64 - Interlocked.Read(ref lastWantedTick) < IdleCloseMs;

    private void Wake()
    {
        try
        {
            signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            if (!IsWanted)
            {
                status = TwitchChatStatus.Idle;
                attempt = 0;
                try
                {
                    await signal.WaitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            status = attempt == 0 ? TwitchChatStatus.Connecting : TwitchChatStatus.Reconnecting;
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            var connectedForAWhile = false;
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(ChatUri, connectTimeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref connectionGeneration);
                status = TwitchChatStatus.Connected;
                lastError = string.Empty;
                var connectedAt = Environment.TickCount64;
                await PumpAsync(socket).ConfigureAwait(false);
                connectedForAWhile = Environment.TickCount64 - connectedAt > 30_000;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.GetBaseException().Message;
            }

            if (stop.IsCancellationRequested)
                break;

            // Closed because nobody is watching: wait quietly for the next Want.
            if (!IsWanted)
                continue;

            if (connectedForAWhile)
                attempt = 0;
            status = TwitchChatStatus.Reconnecting;
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

        status = TwitchChatStatus.Idle;
    }

    private async Task PumpAsync(ClientWebSocket socket)
    {
        while (outbox.TryDequeue(out _))
        {
        }

        // Tags give names, colors, emotes and message ids; commands give deletions, timeouts and chat modes.
        await SendLineAsync(socket, "CAP REQ :twitch.tv/tags twitch.tv/commands", stop.Token).ConfigureAwait(false);
        await SendLineAsync(socket, $"NICK justinfan{Random.Shared.Next(10_000, 99_999)}", stop.Token).ConfigureAwait(false);

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
                // Twitch is gone already.
            }
        }

        if (receive.IsFaulted)
            await receive.ConfigureAwait(false);
        if (send.IsFaulted)
            await send.ConfigureAwait(false);
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var pending = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return;

            var count = decoder.GetChars(buffer, 0, result.Count, chars, 0, flush: false);
            pending.Append(chars, 0, count);
            if (!result.EndOfMessage)
                continue;

            var text = pending.ToString();
            pending.Clear();
            foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!HandleLine(line))
                    return;
            }
        }
    }

    /// <summary>Answers pings and queues everything else. False when Twitch asks to reconnect.</summary>
    private bool HandleLine(string line)
    {
        var message = TwitchIrcMessage.Parse(line);
        if (message == null)
            return true;

        switch (message.Command)
        {
            case "PING":
                outbox.Enqueue($"PONG :{message.Trailing}");
                Wake();
                return true;
            case "RECONNECT":
                return false;
            case "PRIVMSG":
            case "USERNOTICE":
            case "NOTICE":
            case "CLEARMSG":
            case "CLEARCHAT":
            case "ROOMSTATE":
                inbox.Enqueue(message);
                while (inbox.Count > MaxQueuedLines && inbox.TryDequeue(out _))
                {
                }

                return true;
            default:
                return true;
        }
    }

    private async Task SendAsync(ClientWebSocket socket, CancellationToken token)
    {
        var joined = string.Empty;
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await signal.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            while (outbox.TryDequeue(out var text))
                await SendLineAsync(socket, text, token).ConfigureAwait(false);

            if (!IsWanted)
                return;

            var wanted = wantedChannel;
            if (wanted == joined)
                continue;

            if (!string.IsNullOrEmpty(joined))
                await SendLineAsync(socket, $"PART #{joined}", token).ConfigureAwait(false);
            await SendLineAsync(socket, $"JOIN #{wanted}", token).ConfigureAwait(false);
            joined = wanted;
        }
    }

    private static Task SendLineAsync(ClientWebSocket socket, string line, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }
}
