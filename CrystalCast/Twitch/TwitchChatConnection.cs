using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace CrystalCast.Twitch;

/// <summary>A Twitch login for chat: the account's login name and an access token that may read chat.</summary>
internal sealed record TwitchChatLogin(string Login, string AccessToken);

internal enum TwitchChatStatus
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>
/// A read-only connection to Twitch chat. With the user's login (when their token may read chat) it connects as
/// them, so they show in the channel's list of who's in chat; otherwise as an anonymous "justinfan" guest, which
/// Twitch allows anyone. Sending goes through Twitch's API, not this connection. Runs on a background task;
/// parsed lines are queued for the game's main thread.
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
    private readonly Func<CancellationToken, Task<TwitchChatLogin?>>? getLogin;

    /// <summary>Who the tab wants chat to connect as: a login name, or empty for a guest.</summary>
    private volatile string wantedIdentity = string.Empty;
    /// <summary>Who the open connection is logged in as; empty for a guest.</summary>
    private volatile string connectedIdentity = string.Empty;
    /// <summary>A login Twitch's chat refused; chat connects as a guest until the identity changes.</summary>
    private volatile string refusedIdentity = string.Empty;
    private volatile bool reconnectNow;

    public TwitchChatConnection(Func<CancellationToken, Task<TwitchChatLogin?>>? getLogin = null)
    {
        this.getLogin = getLogin;
        loop = Task.Run(RunAsync);
    }

    /// <summary>True while connected as the user rather than as a guest.</summary>
    public bool ConnectedAsUser => status == TwitchChatStatus.Connected && connectedIdentity.Length > 0;

    /// <summary>True when Twitch's chat refused the user's login, so chat is read as a guest.</summary>
    public bool LoginRefused => refusedIdentity.Length > 0 && refusedIdentity == wantedIdentity;

    /// <summary>The identity to connect as: the wanted one, unless Twitch refused it.</summary>
    internal static string EffectiveIdentity(string wanted, string refused) =>
        wanted.Length > 0 && wanted != refused ? wanted : string.Empty;

    public TwitchChatStatus Status => status;
    public string LastError => lastError;

    /// <summary>Bumped on every new connection, so the tab knows to re-show "joining".</summary>
    public long ConnectionGeneration => Interlocked.Read(ref connectionGeneration);

    /// <summary>
    /// Called every frame the tab shows a channel's chat. Joins it, leaving the previous one.
    /// <paramref name="identity"/> is the login to connect as (empty for a guest); a change reconnects.
    /// </summary>
    public void Want(string channel, string identity = "")
    {
        Interlocked.Exchange(ref lastWantedTick, Environment.TickCount64);
        if (wantedIdentity != identity)
        {
            wantedIdentity = identity;
            if (status == TwitchChatStatus.Connected && connectedIdentity != EffectiveIdentity(identity, refusedIdentity))
            {
                reconnectNow = true;
                if (signal.CurrentCount == 0)
                    Wake();
            }
        }

        channel = channel.ToLowerInvariant();
        if (wantedChannel == channel && status != TwitchChatStatus.Idle)
            return;

        wantedChannel = channel;
        // One pending wake is enough; asking every frame must not pile them up.
        if (signal.CurrentCount == 0)
            Wake();
    }

    /// <summary>
    /// Closes the connection now instead of a minute after the tab stops asking, for when the screen is turned
    /// off. The next <see cref="Want"/> opens it again.
    /// </summary>
    public void Release()
    {
        if (string.IsNullOrEmpty(wantedChannel))
            return;

        wantedChannel = string.Empty;
        Interlocked.Exchange(ref lastWantedTick, 0);
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
            reconnectNow = false;
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            var connectedForAWhile = false;
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var login = await GetLoginAsync(connectTimeout.Token).ConfigureAwait(false);
                await socket.ConnectAsync(ChatUri, connectTimeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref connectionGeneration);
                connectedIdentity = login?.Login ?? string.Empty;
                status = TwitchChatStatus.Connected;
                lastError = string.Empty;
                var connectedAt = Environment.TickCount64;
                await PumpAsync(socket, login).ConfigureAwait(false);
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

            connectedIdentity = string.Empty;

            // Closed because nobody is watching: wait quietly for the next Want.
            if (!IsWanted)
                continue;

            // The login changed, or Twitch refused it: connect again right away.
            if (reconnectNow)
            {
                attempt = 0;
                continue;
            }

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

    /// <summary>The user's chat login when the tab wants it and Twitch hasn't refused it; null for a guest.</summary>
    private async Task<TwitchChatLogin?> GetLoginAsync(CancellationToken cancellationToken)
    {
        var identity = EffectiveIdentity(wantedIdentity, refusedIdentity);
        if (identity.Length == 0 || getLogin == null)
            return null;

        try
        {
            var login = await getLogin(cancellationToken).ConfigureAwait(false);
            return login != null && string.Equals(login.Login, identity, StringComparison.OrdinalIgnoreCase) ? login : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task PumpAsync(ClientWebSocket socket, TwitchChatLogin? login)
    {
        while (outbox.TryDequeue(out _))
        {
        }

        // Tags give names, colors, emotes and message ids; commands give deletions, timeouts and chat modes.
        await SendLineAsync(socket, "CAP REQ :twitch.tv/tags twitch.tv/commands", stop.Token).ConfigureAwait(false);
        if (login != null)
        {
            // The token goes only to Twitch's chat server, over this encrypted connection, and is never logged.
            await SendLineAsync(socket, $"PASS oauth:{login.AccessToken}", stop.Token).ConfigureAwait(false);
            await SendLineAsync(socket, $"NICK {login.Login.ToLowerInvariant()}", stop.Token).ConfigureAwait(false);
        }
        else
        {
            await SendLineAsync(socket, $"NICK justinfan{Random.Shared.Next(10_000, 99_999)}", stop.Token).ConfigureAwait(false);
        }

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
            case "NOTICE" when IsLoginRefusal(message):
                // Twitch closes the connection after this; the next one goes in as a guest.
                refusedIdentity = connectedIdentity;
                reconnectNow = true;
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

    /// <summary>Twitch's answer to a login it won't take on chat, such as a token without chat:read.</summary>
    internal static bool IsLoginRefusal(TwitchIrcMessage message) =>
        message.Command == "NOTICE"
        && (message.Trailing.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase)
            || message.Trailing.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase));

    private async Task SendAsync(ClientWebSocket socket, CancellationToken token)
    {
        var joined = string.Empty;
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await signal.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            while (outbox.TryDequeue(out var text))
                await SendLineAsync(socket, text, token).ConfigureAwait(false);

            if (!IsWanted || reconnectNow)
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
