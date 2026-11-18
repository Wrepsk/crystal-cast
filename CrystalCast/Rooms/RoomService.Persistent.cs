using CrystalCast.Ipc;

namespace CrystalCast.Rooms;

/// <summary>
/// Persistent rooms: rooms that stay until their owner deletes them or everyone leaves. Everyone connected
/// shares their own screens (unless they turned that off), and sees everyone else's where the sharer is.
/// The room list is remembered, and rooms with auto-join on reconnect when the plugin starts.
/// </summary>
internal sealed partial class RoomService
{
    /// <summary>How long someone's screens stay after they went offline, so a short drop doesn't flicker.</summary>
    public const long OwnerOfflineGraceMs = 60_000;

    /// <summary>Most of your own screens shared into one room; matches the relay.</summary>
    public const int MaxSharedScreensPerRoom = 8;

    private readonly List<PersistentSession> persistent = [];
    private Task<PersistentRoomCreateResponse>? creatingPersistent;

    public IReadOnlyList<PersistentSession> Persistent => persistent;

    public bool IsCreatingPersistent => creatingPersistent != null;

    /// <summary>The last create or join that failed, or a room that dropped you, for the window to show.</summary>
    public string PersistentNotice { get; private set; } = string.Empty;

    public PersistentSession? FindPersistent(string code) =>
        persistent.FirstOrDefault(session => session.Code == code);

    public bool CreatePersistent(string name, string password, out string error)
    {
        error = string.Empty;
        if (creatingPersistent != null)
            return true;

        if (configuration.PersistentRooms.Count >= RoomMemberKeys.MaxRooms)
        {
            error = $"You can be in up to {RoomMemberKeys.MaxRooms} persistent rooms.";
            return false;
        }

        if (!RoomRelayApi.TryResolveRelay(configuration.RoomRelayUrl, out var relay))
        {
            error = "The relay address in Settings > Rooms isn't valid.";
            return false;
        }

        PersistentNotice = string.Empty;
        creatingPersistent = RoomRelayApi.CreatePersistentRoomAsync(
            relay, name, password, configuration.RoomMemberKey, configuration.RoomUsername, CancellationToken.None);
        return true;
    }

    /// <summary>Joins a room by code. The password is needed only the first time.</summary>
    public bool JoinPersistent(string input, string password, out string error)
    {
        error = string.Empty;
        if (!RoomCode.TryNormalize(input, out var code))
        {
            error = $"A room code has {RoomCode.Length} letters and numbers, like ABCD-2345.";
            return false;
        }

        if (FindPersistent(code) is { } existing)
        {
            if (existing.Connection == null)
                Connect(existing, password);
            return true;
        }

        if (configuration.PersistentRooms.Count >= RoomMemberKeys.MaxRooms)
        {
            error = $"You can be in up to {RoomMemberKeys.MaxRooms} persistent rooms.";
            return false;
        }

        if (!RoomRelayApi.TryResolveRelay(configuration.RoomRelayUrl, out _))
        {
            error = "The relay address in Settings > Rooms isn't valid.";
            return false;
        }

        PersistentNotice = string.Empty;
        var known = configuration.PersistentRooms.FirstOrDefault(entry => entry.Code == code);
        var session = new PersistentSession(code) { Pending = known == null };
        persistent.Add(session);
        Connect(session, password);
        return true;
    }

    public void ConnectPersistent(string code)
    {
        if (FindPersistent(code) is { Connection: null } session)
            Connect(session, string.Empty);
    }

    /// <summary>Stops taking part for now; the room stays in your list.</summary>
    public void DisconnectPersistent(string code)
    {
        if (FindPersistent(code) is { } session)
            Disconnect(session);
    }

    /// <summary>Leaves for good: the relay forgets you, and the room leaves your list.</summary>
    public void LeavePersistent(string code)
    {
        var session = FindPersistent(code);
        if (session?.Connection is { Status: RoomConnectionStatus.Connected } connection)
            connection.Send(new { type = "leave" });

        Forget(code, session);
    }

    /// <summary>Owner only: deletes the room for everyone.</summary>
    public void DeletePersistent(string code)
    {
        if (FindPersistent(code) is { Role: "owner", Connection: { } connection })
            connection.Send(new { type = "delete" });
    }

    public void SetAutoJoin(string code, bool autoJoin)
    {
        var entry = configuration.PersistentRooms.FirstOrDefault(candidate => candidate.Code == code);
        if (entry == null || entry.AutoJoin == autoJoin)
            return;

        entry.AutoJoin = autoJoin;
        configuration.Save();
    }

    /// <summary>kick, ban, unban, promote, demote or transfer; the relay checks who may do what.</summary>
    public void Moderate(string code, string action, string memberId)
    {
        if (action is "kick" or "ban" or "unban" or "promote" or "demote" or "transfer")
            FindPersistent(code)?.Connection?.Send(new { type = action, id = memberId });
    }

    public void SetScreenHidden(string code, string owner, string screenId, bool hide) =>
        FindPersistent(code)?.Connection?.Send(new { type = hide ? "hide" : "unhide", owner, screenId });

    /// <summary>Owner only. Empty removes the password; people already in the room stay in.</summary>
    public void SetPersistentPassword(string code, string password) =>
        FindPersistent(code)?.Connection?.Send(new { type = "password", password = RoomMemberKeys.HashPassword(password) });

    public void RenamePersistent(string code, string name) =>
        FindPersistent(code)?.Connection?.Send(new { type = "roomName", name });

    /// <summary>Turns sharing one of your screens into persistent rooms on or off.</summary>
    public void SetSharedInPersistentRooms(BrowserScreenProfile screen, bool share)
    {
        if (screen.ShareInPersistentRooms == share)
            return;

        screen.ShareInPersistentRooms = share;
        configuration.Save();
    }

    private void StartPersistentRooms()
    {
        foreach (var entry in configuration.PersistentRooms)
        {
            var session = new PersistentSession(entry.Code) { RoomName = entry.Name };
            persistent.Add(session);
            if (entry.AutoJoin)
                Connect(session, string.Empty);
        }
    }

    private void DisposePersistentRooms()
    {
        foreach (var session in persistent)
            Disconnect(session);

        persistent.Clear();
    }

    private void Connect(PersistentSession session, string password)
    {
        if (session.Connection != null || !RoomRelayApi.TryResolveRelay(configuration.RoomRelayUrl, out var relay))
            return;

        session.Error = string.Empty;
        session.Welcomed = false;
        session.Connection = RoomRelayConnection.ForPersistentRoom(
            relay, session.Code, configuration.RoomMemberKey, configuration.RoomUsername, password);
    }

    private void Disconnect(PersistentSession session)
    {
        session.Connection?.Dispose();
        session.Connection = null;
        session.Welcomed = false;
        session.Published.Clear();
        foreach (var mirror in session.Mirrors.Values)
            HideMirror(mirror);

        session.Mirrors.Clear();
    }

    /// <summary>Drops a room from the list and this session.</summary>
    private void Forget(string code, PersistentSession? session)
    {
        if (session != null)
        {
            // Let a leave or delete message go out before the connection closes.
            var connection = session.Connection;
            session.Connection = null;
            Disconnect(session);
            if (connection != null)
                _ = Task.Delay(500).ContinueWith(_ => connection.Dispose(), TaskScheduler.Default);
            persistent.Remove(session);
        }

        if (configuration.PersistentRooms.RemoveAll(entry => entry.Code == code) > 0)
            configuration.Save();
    }

    private void TickPersistentRooms(RoomLocation location, long now, long unixNow)
    {
        if (creatingPersistent is { IsCompleted: true } creating)
        {
            creatingPersistent = null;
            var created = creating.IsCompletedSuccessfully ? creating.Result : null;
            if (created == null || created.Code.Length == 0)
            {
                PersistentNotice = created?.Error is { Length: > 0 } message
                    ? message
                    : $"Could not reach the relay: {creating.Exception?.GetBaseException().Message ?? "no answer"}";
            }
            else
            {
                // The owner is a member already; no password needed.
                var session = new PersistentSession(created.Code) { Pending = true };
                persistent.Add(session);
                Connect(session, string.Empty);
            }
        }

        foreach (var session in persistent.ToList())
            TickPersistent(session, location, now, unixNow);
    }

    private void TickPersistent(PersistentSession session, RoomLocation location, long now, long unixNow)
    {
        var connection = session.Connection;
        if (connection == null)
            return;

        while (connection.TryDequeue(out var message))
        {
            session.Clock.AddRelayTime(message.RelayTime, now);
            HandlePersistentMessage(session, message, now);
        }

        if (connection.Refused || session.ClosedReason.Length > 0)
        {
            var reason = session.ClosedReason.Length > 0
                ? session.ClosedReason
                : RoomRelayConnection.DescribeRefusal(connection.RefusalReason);
            var label = session.RoomName.Length > 0 ? session.RoomName : RoomCode.Format(session.Code);
            log.Information("Persistent room {Code}: {Reason}", session.Code, reason);

            // Gone, removed, banned or never let in: the room leaves the list. Anything else stays, offline.
            var permanent = session.Pending
                || session.ClosedReason.Length > 0
                || connection.RefusalReason is "gone" or "removed" or "banned";
            if (permanent)
            {
                PersistentNotice = session.Pending ? reason : $"{label}: {reason}";
                Forget(session.Code, session);
            }
            else
            {
                Disconnect(session);
                session.Error = reason;
            }

            session.ClosedReason = string.Empty;
            return;
        }

        if (connection.Status != RoomConnectionStatus.Connected || !session.Welcomed)
            return;

        if (RoomPingSchedule.Due(session.PingsSent, session.LastPingAtMs, now))
        {
            session.PingsSent++;
            session.LastPingAtMs = now;
            connection.Send(new { type = "ping", t = now });
        }

        if (location.IsKnown)
            PublishOwnScreens(session, connection, location, unixNow);

        UpdateMirrors(session, location, now);
    }

    private void HandlePersistentMessage(PersistentSession session, RoomRelayMessage message, long now)
    {
        switch (message.Type)
        {
            case "welcome":
                session.Welcomed = true;
                session.You = message.You;
                session.Role = message.Role;
                session.RoomName = message.Name;
                session.PasswordSet = message.PasswordSet ?? false;
                session.PingsSent = 0;
                session.Error = string.Empty;
                session.RelayNotice = string.Empty;
                session.Connection?.ForgetPassword();
                session.HiddenScreens.Clear();
                foreach (var hidden in message.HiddenScreens ?? [])
                    session.HiddenScreens.Add(ScreenKey(hidden.Owner, hidden.ScreenId));

                // Everything goes out again: the relay may have missed changes while disconnected.
                session.Published.Clear();
                session.StaleOwnScreens = message.Yours ?? [];
                RememberRoom(session);
                break;
            case "state" when message.State != null && message.Owner != session.You && message.ScreenId.Length > 0:
            {
                var key = ScreenKey(message.Owner, message.ScreenId);
                if (!session.Mirrors.TryGetValue(key, out var mirror))
                {
                    mirror = new PersistentMirror(message.Owner, message.ScreenId, RoomMemberKeys.MirrorScreenId(session.Code, message.Owner, message.ScreenId));
                    session.Mirrors[key] = mirror;
                }

                mirror.State = message.State;
                mirror.ReceivedAt = message.ReceivedAt;
                mirror.NewState = true;
                break;
            }

            case "unshare":
                RemoveMirror(session, ScreenKey(message.Owner, message.ScreenId));
                break;
            case "hidden":
            {
                var key = ScreenKey(message.Owner, message.ScreenId);
                if (message.Hidden == true)
                {
                    session.HiddenScreens.Add(key);
                    RemoveMirror(session, key);
                }
                else
                {
                    session.HiddenScreens.Remove(key);
                }

                break;
            }

            case "members" when message.Members != null:
                session.Members = message.Members;
                session.Bans = message.Bans ?? [];
                if (message.Members.FirstOrDefault(member => member.You) is { } me)
                    session.Role = me.Role;
                break;
            case "room":
                session.RoomName = message.Name;
                session.PasswordSet = message.PasswordSet ?? session.PasswordSet;
                RememberRoom(session);
                break;
            case "pong":
                session.Clock.AddPong(message.T, message.RelayTime, now);
                break;
            case "error":
                session.RelayNotice = message.Reason;
                break;
            case "closed":
                session.ClosedReason = string.IsNullOrWhiteSpace(message.Reason) ? "The room closed." : message.Reason;
                break;
            case "add" when message.ScreenId.Length > 0:
                if (configuration.FindBrowserScreen(message.ScreenId) is { } screen && IsShareable(screen)
                    && RoomPlaylistSync.TryAcceptAdd(screen, message.Url, out _))
                {
                    configuration.Save();
                    log.Information("Persistent room {Code}: added {Url} to {Screen} for someone in the room.", session.Code, message.Url, screen.Name);
                }

                break;
        }
    }

    /// <summary>Puts a room in the remembered list once the relay let you in, and keeps its name current.</summary>
    private void RememberRoom(PersistentSession session)
    {
        var entry = configuration.PersistentRooms.FirstOrDefault(candidate => candidate.Code == session.Code);
        if (entry == null)
        {
            entry = new PersistentRoomEntry { Code = session.Code, AutoJoin = true };
            configuration.PersistentRooms.Add(entry);
        }

        session.Pending = false;
        entry.Name = session.RoomName;
        configuration.Save();
    }

    private bool IsShareable(BrowserScreenProfile screen) =>
        configuration.PersistentRoomsShareMyScreens
        && screen.ShareInPersistentRooms
        && screen.Enabled
        && !screen.CreatedByIpc;

    private void PublishOwnScreens(PersistentSession session, RoomRelayConnection connection, RoomLocation location, long unixNow)
    {
        var shareable = configuration.BrowserScreens.Where(IsShareable).Take(MaxSharedScreensPerRoom).ToList();
        var ids = shareable.Select(screen => screen.ScreenId).ToHashSet(StringComparer.Ordinal);
        var tick = Environment.TickCount64;

        foreach (var screen in shareable)
        {
            if (ipc.IsReloadingForSignIn(screen) || !ipc.TryBuildRoomState(screen, out var envelope, out var placement))
                continue;

            if (!session.Published.TryGetValue(screen.ScreenId, out var published))
            {
                published = new PersistentPublished();
                session.Published[screen.ScreenId] = published;
            }

            var state = BuildHostState(screen, envelope, placement, location, unixNow);
            var buffering = published.Stall.Update(
                envelope.Playback.State == ScreenPlaybackState.Playing,
                envelope.Playback.PositionMs,
                envelope.Playback.HostTimestampUnixMs);
            if (published.Policy.Decide(state, tick, buffering))
                connection.Send(new { type = "state", screenId = screen.ScreenId, state });
        }

        foreach (var screenId in session.Published.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            session.Published.Remove(screenId);
            connection.Send(new { type = "unshare", screenId });
        }

        // Screens the relay still had from before that this CrystalCast doesn't share anymore.
        foreach (var screenId in session.StaleOwnScreens.Where(id => !ids.Contains(id)))
            connection.Send(new { type = "unshare", screenId });
        session.StaleOwnScreens = [];
    }

    private void UpdateMirrors(PersistentSession session, RoomLocation location, long now)
    {
        var relayNow = session.Clock.RelayNow(now);
        foreach (var (key, mirror) in session.Mirrors.ToList())
        {
            var owner = session.Members.FirstOrDefault(member => member.Id == mirror.Owner);
            if (owner is { Online: true })
            {
                mirror.OwnerOfflineSinceMs = 0;
            }
            else if (session.Members.Count > 0)
            {
                if (mirror.OwnerOfflineSinceMs == 0)
                    mirror.OwnerOfflineSinceMs = now;
                else if (now - mirror.OwnerOfflineSinceMs >= OwnerOfflineGraceMs)
                {
                    RemoveMirror(session, key);
                    continue;
                }
            }

            var show = configuration.PersistentRoomsShowOthersScreens
                && !session.HiddenScreens.Contains(key)
                && !session.Dismissed.Contains(mirror.LocalScreenId)
                && mirror.State != null;
            if (!show)
            {
                HideMirror(mirror);
                continue;
            }

            // Between areas (loading screens) nothing changes, so a copy isn't torn down and rebuilt for nothing.
            if (!location.IsKnown)
                continue;

            // A copy exists only where its screen is: elsewhere it would just sit in the screen list, turned off.
            var state = mirror.State!;
            if (!location.SamePlaceAs(state.Location))
            {
                HideMirror(mirror);
                mirror.WasInPlace = false;
                continue;
            }

            var placeChanged = !mirror.WasInPlace || !mirror.Shown;
            mirror.WasInPlace = true;

            var (localPosition, buffering) = mirror.Shown ? ReadLocalPlayback(mirror.LocalScreenId, mirror.Stall) : (null, false);
            var checkDrift = now - mirror.LastDriftCheckMs >= DriftCheckMs;
            if (checkDrift)
                mirror.LastDriftCheckMs = now;

            var drifted = checkDrift && RoomViewerPlan.HasDrifted(
                RoomViewerPlan.ExpectedPositionMs(state, mirror.ReceivedAt, relayNow), localPosition, now, mirror.Applied, buffering);
            if (!mirror.NewState && !placeChanged && !drifted)
                continue;

            var ownerName = owner?.Name is { Length: > 0 } name ? name : "someone";
            var roomLabel = session.RoomName.Length > 0 ? session.RoomName : RoomCode.Format(session.Code);
            var target = new RoomMirrorTarget(
                mirror.LocalScreenId,
                $"{ownerName}'s screen",
                $"{ownerName} in {roomLabel}",
                NameSuffix: $"({ownerName})");
            var request = RoomViewerPlan.Build(target, state, mirror.ReceivedAt, relayNow, location, localPosition, now, mirror.Applied, buffering);
            var result = IpcJsonService.Deserialize<ScreenIpcMutationResponse>(ipc.ApplyRoomUpdate(request));
            session.Error = result is { Success: false } ? result.Error : string.Empty;
            mirror.Shown = result is not { Success: false };

            if ((mirror.NewState || placeChanged) && configuration.FindBrowserScreen(mirror.LocalScreenId) is { } copy
                && RoomPlaylistSync.Mirror(copy, state.Playlist))
            {
                configuration.Save();
            }

            mirror.NewState = false;
        }
    }

    private void RemoveMirror(PersistentSession session, string key)
    {
        if (session.Mirrors.Remove(key, out var mirror))
            HideMirror(mirror);
    }

    /// <summary>Takes the local copy away; it comes back from the stored state when shown again.</summary>
    private void HideMirror(PersistentMirror mirror)
    {
        if (!mirror.Shown)
            return;

        ipc.RemoveRoomScreen(mirror.LocalScreenId);
        mirror.Shown = false;
        mirror.Applied.Url = string.Empty;
        mirror.Applied.Paused = null;
        mirror.Applied.SeekStreak = 0;
        mirror.NewState = true;
    }

    /// <summary>The user deleted a copy of someone's screen: keep it away for this session.</summary>
    private bool OnPersistentMirrorDeleted(BrowserScreenProfile screen)
    {
        foreach (var session in persistent)
        {
            var mirror = session.Mirrors.Values.FirstOrDefault(candidate => candidate.LocalScreenId == screen.ScreenId);
            if (mirror == null)
                continue;

            session.Dismissed.Add(screen.ScreenId);
            mirror.Shown = false;
            mirror.Applied.Url = string.Empty;
            mirror.Applied.Paused = null;
            return true;
        }

        return false;
    }

    /// <summary>Brings back copies dismissed in a room.</summary>
    public void RestoreDismissed(string code)
    {
        if (FindPersistent(code) is { } session)
            session.Dismissed.Clear();
    }

    /// <summary>Playlist links for a copy of someone's screen from a persistent room go to its owner.</summary>
    private bool TryRequestPersistentAdd(BrowserScreenProfile screen, string input, out bool handled, out string error)
    {
        handled = false;
        error = string.Empty;
        foreach (var session in persistent)
        {
            var mirror = session.Mirrors.Values.FirstOrDefault(candidate => candidate.LocalScreenId == screen.ScreenId);
            if (mirror == null)
                continue;

            if (mirror.State?.Playlist.AllowAdds != true)
                error = "The person sharing this screen isn't taking videos right now.";
            else if (session.Connection is not { Status: RoomConnectionStatus.Connected } connection)
                error = "Not connected to the room right now.";
            else if (RoomPlaylistSync.TryValidateLink(input, out var url, out error))
            {
                connection.Send(new { type = "add", owner = mirror.Owner, screenId = mirror.ScreenId, url });
                handled = true;
            }

            return true;
        }

        return false;
    }

    private static string ScreenKey(string owner, string screenId) => $"{owner}:{screenId}";
}

/// <summary>One persistent room in your list, connected or not.</summary>
internal sealed class PersistentSession(string code)
{
    public string Code { get; } = code;
    public RoomRelayConnection? Connection { get; set; }
    public RoomClock Clock { get; } = new();

    /// <summary>Joined or created just now and not let in yet; it isn't in the remembered list until it is.</summary>
    public bool Pending { get; set; }
    public bool Welcomed { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public bool PasswordSet { get; set; }

    /// <summary>Your member id in this room.</summary>
    public string You { get; set; } = string.Empty;

    /// <summary>"owner", "mod" or "member".</summary>
    public string Role { get; set; } = string.Empty;
    public bool IsOwner => Role == "owner";
    public bool IsModerator => Role is "owner" or "mod";
    public IReadOnlyList<RoomMember> Members { get; set; } = [];
    public IReadOnlyList<PersistentBan> Bans { get; set; } = [];
    public Dictionary<string, PersistentPublished> Published { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PersistentMirror> Mirrors { get; } = new(StringComparer.Ordinal);

    /// <summary>Screens a moderator hid for everyone, as owner:screenId.</summary>
    public HashSet<string> HiddenScreens { get; } = new(StringComparer.Ordinal);

    /// <summary>Copies you deleted yourself; they stay away until you bring them back or reconnect.</summary>
    public HashSet<string> Dismissed { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> StaleOwnScreens { get; set; } = [];
    public int PingsSent { get; set; }
    public long LastPingAtMs { get; set; }
    public string ClosedReason { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    /// <summary>The relay's answer to something you tried that it didn't allow.</summary>
    public string RelayNotice { get; set; } = string.Empty;

    public bool IsHiddenForEveryone(string owner, string screenId) => HiddenScreens.Contains($"{owner}:{screenId}");
}

/// <summary>One of your screens shared into a persistent room.</summary>
internal sealed class PersistentPublished
{
    public RoomHostSendPolicy Policy { get; } = new();
    public PlaybackStallTracker Stall { get; } = new();
}

/// <summary>Someone else's screen in a persistent room, and its local copy.</summary>
internal sealed class PersistentMirror(string owner, string screenId, string localScreenId)
{
    public string Owner { get; } = owner;
    public string ScreenId { get; } = screenId;
    public string LocalScreenId { get; } = localScreenId;
    public RoomScreenState? State { get; set; }
    public long ReceivedAt { get; set; }
    public bool NewState { get; set; }
    public bool Shown { get; set; }
    public bool WasInPlace { get; set; }
    public long LastDriftCheckMs { get; set; }
    public long OwnerOfflineSinceMs { get; set; }
    public RoomViewerApplied Applied { get; } = new();
    public PlaybackStallTracker Stall { get; } = new();
}
