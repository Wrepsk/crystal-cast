using CrystalCast.Ipc;
using Dalamud.Plugin.Services;

namespace CrystalCast.Rooms;

/// <summary>
/// Sync rooms: share one of your screens under a room code, or join someone else's. The host's CrystalCast
/// sends the screen's state to a relay; everyone who joined with the code gets a copy of the screen that
/// follows it. Only the host controls it, and the copy only shows where the host is.
/// Everything runs on the game's main thread; the relay connections queue their messages for it.
/// </summary>
internal sealed partial class RoomService : IDisposable
{
    private const long TickMs = 250;
    private const long DriftCheckMs = 2_000;

    private readonly Configuration configuration;
    private readonly ScreenStateIpc ipc;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly List<HostedRoom> hosted = [];
    private readonly List<JoinedRoom> joined = [];
    private long lastTickMs;

    public RoomService(
        Configuration configuration,
        ScreenStateIpc ipc,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.ipc = ipc;
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;

        // Joined screens belong to a session; ones left over from the last time the plugin ran have no room.
        if (configuration.BrowserScreens.RemoveAll(IsRoomScreen) > 0)
        {
            configuration.Normalize();
            configuration.Save();
        }

        framework.Update += OnFrameworkUpdate;
        StartPersistentRooms();
    }

    public IReadOnlyList<HostedRoom> Hosted => hosted;
    public IReadOnlyList<JoinedRoom> Joined => joined;

    public static bool IsRoomScreen(BrowserScreenProfile screen) =>
        screen.CreatedByIpc && string.Equals(screen.IpcOwnerId, RoomViewerPlan.OwnerId, StringComparison.Ordinal);

    public bool HasRelay => RoomRelayApi.TryResolveRelay(configuration.RoomRelayUrl, out _);

    public HostedRoom? FindHosted(string screenId) =>
        hosted.FirstOrDefault(room => string.Equals(room.ScreenId, screenId, StringComparison.Ordinal));

    /// <summary>Starts sharing <paramref name="screen"/>. The room code arrives a moment later.</summary>
    public bool Share(BrowserScreenProfile screen, out string error)
    {
        error = string.Empty;
        if (IsRoomScreen(screen))
        {
            error = "This screen comes from a room you joined; only its host can share it.";
            return false;
        }

        if (screen.CreatedByIpc)
        {
            error = "Screens created by other plugins can't be shared.";
            return false;
        }

        if (FindHosted(screen.ScreenId) != null)
            return true;

        if (!RoomRelayApi.TryResolveRelay(configuration.RoomRelayUrl, out var relay))
        {
            error = "The relay address in Settings > Rooms isn't valid.";
            return false;
        }

        var room = new HostedRoom(screen.ScreenId, relay);
        room.Creating = RoomRelayApi.CreateRoomAsync(relay, CancellationToken.None);
        hosted.Add(room);
        return true;
    }

    public void StopSharing(string screenId)
    {
        var room = FindHosted(screenId);
        if (room == null)
            return;

        room.Connection?.Send(new { type = "close" });
        // Give the close message a moment to leave before the connection goes.
        var connection = room.Connection;
        hosted.Remove(room);
        if (connection != null)
            _ = Task.Delay(500).ContinueWith(_ => connection.Dispose(), TaskScheduler.Default);
    }

    public bool Join(string input, out string error)
    {
        error = string.Empty;
        if (!RoomCode.TryNormalize(input, out var code))
        {
            error = $"A room code has {RoomCode.Length} letters and numbers, like ABCD-2345.";
            return false;
        }

        if (hosted.Any(room => room.Code == code))
        {
            error = "That's your own room.";
            return false;
        }

        if (joined.Any(room => room.Code == code))
            return true;

        if (!RoomRelayApi.TryResolveRelay(configuration.RoomRelayUrl, out var relay))
        {
            error = "The relay address in Settings > Rooms isn't valid.";
            return false;
        }

        joined.Add(new JoinedRoom(code, new RoomRelayConnection(relay, code, hostKey: null, configuration.RoomUsername)));
        return true;
    }

    public void Leave(string code) => Leave(code, removeScreen: true);

    /// <summary>The username changed in Settings: tell every room you're in.</summary>
    public void Rename(string name)
    {
        foreach (var room in hosted)
            room.Connection?.Rename(name);
        foreach (var room in joined)
            room.Connection.Rename(name);
        foreach (var session in persistent)
            session.Connection?.Rename(name);
    }

    /// <summary>The user deleted a joined room's screen: leave that room too.</summary>
    public void OnRoomScreenDeleted(BrowserScreenProfile screen)
    {
        if (OnPersistentMirrorDeleted(screen))
            return;

        var room = joined.FirstOrDefault(candidate => RoomCode.ScreenIdFor(candidate.Code) == screen.ScreenId);
        if (room != null)
            Leave(room.Code, removeScreen: false);
    }

    /// <summary>A shared screen was deleted: stop sharing it.</summary>
    public void OnScreenDeleted(string screenId) => StopSharing(screenId);

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        foreach (var room in hosted.ToList())
            StopSharing(room.ScreenId);

        foreach (var room in joined.ToList())
            Leave(room.Code, removeScreen: true);

        DisposePersistentRooms();
    }

    private void Leave(string code, bool removeScreen)
    {
        var room = joined.FirstOrDefault(candidate => candidate.Code == code);
        if (room == null)
            return;

        joined.Remove(room);
        room.Connection.Dispose();
        if (removeScreen)
            ipc.RemoveRoomScreen(RoomCode.ScreenIdFor(code));
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now - lastTickMs < TickMs)
            return;

        lastTickMs = now;
        if (hosted.Count == 0 && joined.Count == 0 && persistent.Count == 0 && creatingPersistent == null)
            return;

        try
        {
            var location = GameRoomLocation.Read(clientState, objectTable);
            var unixNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var room in hosted.ToList())
                TickHost(room, location, unixNow);

            foreach (var room in joined.ToList())
                TickViewer(room, location, now);

            TickPersistentRooms(location, now, unixNow);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CrystalCast rooms update failed.");
        }
    }

    private void TickHost(HostedRoom room, RoomLocation location, long unixNow)
    {
        var screen = configuration.FindBrowserScreen(room.ScreenId);
        if (screen == null)
        {
            StopSharing(room.ScreenId);
            return;
        }

        if (room.Creating != null)
        {
            if (!room.Creating.IsCompleted)
                return;

            var creating = room.Creating;
            room.Creating = null;
            var created = creating.IsCompletedSuccessfully ? creating.Result : null;
            if (created == null || string.IsNullOrEmpty(created.Code))
            {
                room.Error = created?.Error is { Length: > 0 } message
                    ? message
                    : $"Could not reach the relay: {creating.Exception?.GetBaseException().Message ?? "no answer"}";
                return;
            }

            room.Code = created.Code;
            room.Connection = new RoomRelayConnection(room.Relay, created.Code, created.HostKey, configuration.RoomUsername);
        }

        var connection = room.Connection;
        if (connection == null)
            return;

        while (connection.TryDequeue(out var message))
        {
            if (message.Type == "welcome")
            {
                room.Viewers = message.Viewers ?? 0;
                room.Policy = new RoomHostSendPolicy();
            }
            else if (message.Type == "viewers")
            {
                room.Viewers = message.Count ?? room.Viewers;
            }
            else if (message.Type == "members" && message.Members != null)
            {
                room.Members = message.Members;
            }
            else if (message.Type == "add")
            {
                // The change goes out with the next state, like any other playlist edit.
                if (RoomPlaylistSync.TryAcceptAdd(screen, message.Url, out var addError))
                {
                    configuration.Save();
                    log.Information("Room {Code}: added {Url} to the playlist for someone in the room.", room.Code, message.Url);
                }
                else
                {
                    log.Debug("Room {Code}: did not add {Url}: {Error}", room.Code, message.Url, addError);
                }
            }
        }

        if (connection.Refused)
        {
            room.Error = "The relay closed this room.";
            return;
        }

        // A login on the host's screen: viewers keep playing on their own until the host's player is back.
        if (connection.Status != RoomConnectionStatus.Connected
            || !location.IsKnown
            || ipc.IsReloadingForSignIn(screen)
            || !ipc.TryBuildRoomState(screen, out var envelope, out var placement))
        {
            return;
        }

        var state = BuildHostState(screen, envelope, placement, location, unixNow);
        var buffering = room.Stall.Update(
            envelope.Playback.State == ScreenPlaybackState.Playing,
            envelope.Playback.PositionMs,
            envelope.Playback.HostTimestampUnixMs);
        if (room.Policy.Decide(state, Environment.TickCount64, buffering))
            connection.Send(new { type = "state", change = true, state });
    }

    internal static RoomScreenState BuildHostState(
        BrowserScreenProfile screen,
        ScreenStateEnvelope envelope,
        Rendering.ResolvedScreenPlacement placement,
        RoomLocation location,
        long unixNow)
    {
        var playback = envelope.Playback;
        var playing = playback.State == ScreenPlaybackState.Playing;
        var position = playback.PositionMs;
        if (playing && playback.HostTimestampUnixMs > 0)
            position += (long)(Math.Max(0, unixNow - playback.HostTimestampUnixMs) * Math.Max(0f, playback.Rate));

        return new RoomScreenState
        {
            Name = screen.Name,
            Location = location,
            PositionX = placement.Position.X,
            PositionY = placement.Position.Y,
            PositionZ = placement.Position.Z,
            YawRadians = placement.YawRadians,
            PitchRadians = placement.PitchRadians,
            RollRadians = placement.RollRadians,
            WidthMeters = envelope.SizeMeters.X,
            HeightMeters = envelope.SizeMeters.Y,
            Visual = envelope.Visual,
            Provider = screen.ProviderKind,
            Url = envelope.Source.Url,
            Paused = !playing,
            PositionMs = Math.Max(0, position),
            Rate = playback.Rate,
            Loop = playback.Loop,
            PlaylistAutoplayNext = playback.PlaylistAutoplayNext,
            Playlist = RoomPlaylistSync.BuildState(screen),
        };
    }

    private void TickViewer(JoinedRoom room, RoomLocation location, long now)
    {
        var newState = false;
        while (room.Connection.TryDequeue(out var message))
        {
            room.Clock.AddRelayTime(message.RelayTime, now);
            switch (message.Type)
            {
                case "welcome":
                    room.HostConnected = message.HostConnected ?? false;
                    room.PingsSent = 0;
                    if (message.State != null)
                    {
                        room.State = message.State;
                        room.ReceivedAt = message.ReceivedAt;
                        newState = true;
                    }

                    break;
                case "state" when message.State != null:
                    room.State = message.State;
                    room.ReceivedAt = message.ReceivedAt;
                    room.HostConnected = true;
                    newState = true;
                    break;
                case "host":
                    room.HostConnected = message.Connected ?? room.HostConnected;
                    break;
                case "pong":
                    room.Clock.AddPong(message.T, message.RelayTime, now);
                    break;
                case "members" when message.Members != null:
                    room.Members = message.Members;
                    break;
                case "closed":
                    room.ClosedReason = string.IsNullOrWhiteSpace(message.Reason) ? "The room closed." : message.Reason;
                    break;
            }
        }

        if (room.Connection.Status == RoomConnectionStatus.Connected)
            room.HostPresence.Observe(room.HostConnected, now);
        else
            room.HostPresence.Reset();

        if (room.ClosedReason.Length == 0 && room.HostPresence.ShouldLeave(now))
            room.ClosedReason = "The host has been offline for 3 minutes.";

        if (room.ClosedReason.Length > 0 || room.Connection.Refused)
        {
            Leave(room.Code, removeScreen: true);
            log.Information("Left room {Code}: {Reason}", room.Code, room.ClosedReason.Length > 0 ? room.ClosedReason : "the relay refused it");
            room.Error = room.ClosedReason.Length > 0 ? room.ClosedReason : "That room doesn't exist or has closed.";
            LastLeftRoom = room;
            return;
        }

        if (room.Connection.Status == RoomConnectionStatus.Connected && RoomPingSchedule.Due(room.PingsSent, room.LastPingAtMs, now))
        {
            room.PingsSent++;
            room.LastPingAtMs = now;
            room.Connection.Send(new { type = "ping", t = now });
        }

        if (room.State == null || !location.IsKnown)
            return;

        // The copy exists only where the host's screen is; elsewhere it would just sit in the screen list.
        var inPlace = location.SamePlaceAs(room.State.Location);
        if (!inPlace)
        {
            room.WasInPlace = false;
            if (room.CopyShown)
            {
                ipc.RemoveRoomScreen(RoomCode.ScreenIdFor(room.Code));
                room.CopyShown = false;
                room.Applied.Url = string.Empty;
                room.Applied.Paused = null;
                room.Applied.SeekStreak = 0;
            }

            return;
        }

        var placeChanged = !room.WasInPlace || !room.CopyShown;
        room.WasInPlace = true;

        var relayNow = room.Clock.RelayNow(now);
        var (localPosition, buffering) = room.CopyShown ? ReadLocalPlayback(RoomCode.ScreenIdFor(room.Code), room.Stall) : (null, false);
        var drifted = now - room.LastDriftCheckMs >= DriftCheckMs
            && RoomViewerPlan.HasDrifted(
                RoomViewerPlan.ExpectedPositionMs(room.State, room.ReceivedAt, relayNow),
                localPosition,
                now,
                room.Applied,
                buffering);
        if (now - room.LastDriftCheckMs >= DriftCheckMs)
            room.LastDriftCheckMs = now;

        if (!newState && !placeChanged && !drifted)
            return;

        var request = RoomViewerPlan.Build(room.Code, room.State, room.ReceivedAt, relayNow, location, localPosition, now, room.Applied, buffering);
        var result = IpcJsonService.Deserialize<ScreenIpcMutationResponse>(ipc.ApplyRoomUpdate(request));
        room.Error = result is { Success: false } ? result.Error : string.Empty;
        room.CopyShown = result is not { Success: false };

        if ((newState || placeChanged) && configuration.FindBrowserScreen(RoomCode.ScreenIdFor(room.Code)) is { } joinedScreen
            && RoomPlaylistSync.Mirror(joinedScreen, room.State.Playlist))
        {
            configuration.Save();
        }
    }

    /// <summary>
    /// Sends a link to the host of the room <paramref name="screen"/> came from, for its playlist. The host's
    /// CrystalCast adds it if adding is still on, and the playlist comes back with the next state.
    /// </summary>
    public bool RequestAdd(BrowserScreenProfile screen, string input, out string error)
    {
        if (TryRequestPersistentAdd(screen, input, out var handled, out error))
            return handled;

        var room = joined.FirstOrDefault(candidate => RoomCode.ScreenIdFor(candidate.Code) == screen.ScreenId);
        if (room == null)
        {
            error = "This screen isn't from a room you're in.";
            return false;
        }

        if (room.State?.Playlist.AllowAdds != true)
        {
            error = "The host isn't taking videos right now.";
            return false;
        }

        if (room.Connection.Status != RoomConnectionStatus.Connected)
        {
            error = "Not connected to the room right now.";
            return false;
        }

        if (!RoomPlaylistSync.TryValidateLink(input, out var url, out error))
            return false;

        room.Connection.Send(new { type = "add", url });
        return true;
    }

    /// <summary>
    /// A room screen's own playback position now, when its player reports one, and whether that player is
    /// buffering.
    /// </summary>
    private (long? PositionMs, bool Buffering) ReadLocalPlayback(string screenId, PlaybackStallTracker stall)
    {
        var screen = configuration.FindBrowserScreen(screenId);
        if (screen == null || !ipc.TryBuildRoomState(screen, out var envelope, out _))
            return (null, false);

        var playback = envelope.Playback;
        var buffering = stall.Update(playback.State == ScreenPlaybackState.Playing, playback.PositionMs, playback.HostTimestampUnixMs);
        if (playback.HostTimestampUnixMs <= 0 || playback.DurationMs <= 0 && playback.PositionMs <= 0)
            return (null, false);

        var position = playback.PositionMs;
        if (playback.State == ScreenPlaybackState.Playing)
        {
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - playback.HostTimestampUnixMs;
            position += (long)(Math.Max(0, elapsed) * Math.Max(0f, playback.Rate));
        }

        return (position, buffering);
    }

    /// <summary>The last room that closed on its own, so the window can say why.</summary>
    public JoinedRoom? LastLeftRoom { get; private set; }
}

internal sealed class HostedRoom(string screenId, Uri relay)
{
    public string ScreenId { get; } = screenId;
    public Uri Relay { get; } = relay;
    public Task<RoomCreateResponse>? Creating { get; set; }
    public string Code { get; set; } = string.Empty;
    public RoomRelayConnection? Connection { get; set; }
    public RoomHostSendPolicy Policy { get; set; } = new();
    public PlaybackStallTracker Stall { get; } = new();
    public int Viewers { get; set; }

    /// <summary>Who is in the room; empty until the relay sends the list (older relays never do).</summary>
    public IReadOnlyList<RoomMember> Members { get; set; } = [];
    public string Error { get; set; } = string.Empty;
}

internal sealed class JoinedRoom(string code, RoomRelayConnection connection)
{
    public string Code { get; } = code;
    public RoomRelayConnection Connection { get; } = connection;
    public RoomClock Clock { get; } = new();
    public RoomViewerApplied Applied { get; } = new();
    public RoomHostPresence HostPresence { get; } = new();
    public RoomScreenState? State { get; set; }
    public long ReceivedAt { get; set; }
    public bool HostConnected { get; set; }

    /// <summary>Who is in the room; empty until the relay sends the list (older relays never do).</summary>
    public IReadOnlyList<RoomMember> Members { get; set; } = [];
    public bool WasInPlace { get; set; }

    /// <summary>Whether the local copy of the host's screen exists; it's removed while you're somewhere else.</summary>
    public bool CopyShown { get; set; }
    public long LastPingAtMs { get; set; }
    public int PingsSent { get; set; }
    public PlaybackStallTracker Stall { get; } = new();
    public long LastDriftCheckMs { get; set; }
    public string ClosedReason { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
