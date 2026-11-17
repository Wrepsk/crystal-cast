using CrystalCast.Rooms;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

/// <summary>The Persistent rooms tab: create or join one, and manage the rooms you're in.</summary>
internal sealed partial class RoomsWindow
{
    private string createName = string.Empty;
    private string createPassword = string.Empty;
    private string persistentCode = string.Empty;
    private string persistentPassword = string.Empty;
    private string persistentError = string.Empty;
    private string expandedCode = string.Empty;
    private string passwordDraft = string.Empty;
    private string roomNameDraft = string.Empty;

    /// <summary>A leave or delete waits for a second click on the same button.</summary>
    private string confirming = string.Empty;
    private string menuMemberId = string.Empty;

    private void DrawPersistent()
    {
        DrawCreatePersistent();
        Ui.VerticalSpace(16.0f);
        DrawJoinPersistent();

        if (persistentError.Length > 0)
            Ui.Text(persistentError, CrystalCastUiTheme.Error, f => f.Small, wrap: true);
        else if (rooms.PersistentNotice.Length > 0)
            Ui.Text(rooms.PersistentNotice, CrystalCastUiTheme.Error, f => f.Small, wrap: true);

        Ui.VerticalSpace(20.0f);
        Ui.SectionHeader("Your rooms", "Your screens are shared in every room you're connected to; change that in Settings > Rooms or per screen in its ... menu.");
        if (rooms.Persistent.Count == 0)
        {
            Ui.Meta("You're not in a persistent room yet.");
            return;
        }

        foreach (var session in rooms.Persistent.ToList())
        {
            ImGui.PushID(session.Code);
            DrawPersistentRoom(session);
            Ui.VerticalSpace(12.0f);
            ImGui.PopID();
        }
    }

    private void DrawCreatePersistent()
    {
        Ui.SectionHeader("Create a room", "You own it: you can set a password, make moderators, and kick or ban people. It stays until you delete it or everyone leaves.");
        var width = Math.Max(S(100.0f), (ImGui.GetContentRegionAvail().X - Ui.ButtonWidth("Create") - S(16.0f)) * 0.5f);
        Ui.TextField("##PersistentName", ref createName, 64, "Room name", width);
        ImGui.SameLine(0.0f, S(8.0f));
        Ui.TextField("##PersistentCreatePassword", ref createPassword, 128, "Password (optional)", width, flags: ImGuiInputTextFlags.Password);
        ImGui.SameLine(0.0f, S(8.0f));
        var creating = rooms.IsCreatingPersistent;
        if (Ui.Button(creating ? "Creating…" : "Create", ButtonKind.Primary, disabled: creating || !rooms.HasRelay))
        {
            if (rooms.CreatePersistent(createName.Trim(), createPassword, out var error))
            {
                createName = string.Empty;
                createPassword = string.Empty;
                persistentError = string.Empty;
            }
            else
            {
                persistentError = error;
            }
        }
    }

    private void DrawJoinPersistent()
    {
        Ui.SectionHeader("Join a room", "Enter the code and, if the room has one, its password. You only need the password the first time.");
        var width = Math.Max(S(100.0f), (ImGui.GetContentRegionAvail().X - Ui.ButtonWidth("Join") - S(16.0f)) * 0.5f);
        var enter = Ui.TextField("##PersistentCode", ref persistentCode, 32, "ABCD-2345", width, mono: true, flags: ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(0.0f, S(8.0f));
        enter |= Ui.TextField("##PersistentJoinPassword", ref persistentPassword, 128, "Password", width, flags: ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(0.0f, S(8.0f));
        if ((Ui.Button("Join##Persistent", ButtonKind.Primary, disabled: !rooms.HasRelay) || enter) && rooms.HasRelay)
        {
            if (rooms.JoinPersistent(persistentCode, persistentPassword, out var error))
            {
                persistentCode = string.Empty;
                persistentPassword = string.Empty;
                persistentError = string.Empty;
            }
            else
            {
                persistentError = error;
            }
        }
    }

    private void DrawPersistentRoom(PersistentSession session)
    {
        var name = session.RoomName.Length > 0 ? session.RoomName : "Persistent room";
        Ui.RowText(name, CrystalCastUiTheme.Text, f => f.SemiBold);
        ImGui.SameLine(0.0f, S(8.0f));
        Ui.RowText(RoomCode.Format(session.Code), Accent, f => f.Mono);
        if (session.Role.Length > 0)
        {
            ImGui.SameLine(0.0f, S(8.0f));
            Ui.RowText(RoleLabel(session.Role), TextTertiary, f => f.Small);
        }

        Ui.Meta(DescribePersistent(session));
        if (session.RelayNotice.Length > 0)
            Ui.Text(session.RelayNotice, CrystalCastUiTheme.Warning, f => f.Small, wrap: true);

        if (session.Connection == null)
        {
            if (Ui.Button("Connect"))
                rooms.ConnectPersistent(session.Code);
        }
        else if (Ui.Button("Disconnect", tooltip: "Stop taking part for now. The room stays in your list."))
        {
            rooms.DisconnectPersistent(session.Code);
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button(copiedCode == session.Code && Environment.TickCount64 - copiedAtTick < 2000 ? "Copied" : "Copy code"))
        {
            ImGui.SetClipboardText(RoomCode.Format(session.Code));
            copiedCode = session.Code;
            copiedAtTick = Environment.TickCount64;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        var expanded = expandedCode == session.Code;
        if (Ui.Button(expanded ? "Less" : "Manage", on: expanded))
            expandedCode = expanded ? string.Empty : session.Code;

        ImGui.SameLine(0.0f, S(12.0f));
        var autoJoin = configuration.PersistentRooms.FirstOrDefault(entry => entry.Code == session.Code)?.AutoJoin ?? true;
        if (Ui.SwitchLabeled("##AutoJoin", "Auto-join", ref autoJoin, tooltip: "Connect to this room when the game starts."))
            rooms.SetAutoJoin(session.Code, autoJoin);

        if (!expanded)
            return;

        Ui.VerticalSpace(8.0f);
        DrawPersistentScreens(session);
        DrawPersistentMembers(session);
        DrawPersistentBans(session);
        DrawOwnerControls(session);
        DrawLeave(session);
    }

    private void DrawPersistentScreens(PersistentSession session)
    {
        var mine = session.Published.Keys
            .Select(id => configuration.FindBrowserScreen(id))
            .Where(screen => screen != null)
            .ToList();
        var others = session.Mirrors.Values.Where(mirror => mirror.State != null).ToList();
        var hidden = session.HiddenScreens.ToList();
        if (mine.Count == 0 && others.Count == 0 && hidden.Count == 0)
        {
            Ui.Meta(session.Connection == null ? "Connect to see the screens in this room." : "Nobody is sharing a screen right now.");
            Ui.VerticalSpace(6.0f);
            return;
        }

        Ui.RowText("Screens", TextTertiary, f => f.Small);
        foreach (var screen in mine)
        {
            var hiddenNote = session.IsHiddenForEveryone(session.You, screen!.ScreenId) ? "  ·  hidden by a moderator" : string.Empty;
            Ui.RowText($"{screen.Name} (you){hiddenNote}", Accent, f => f.Body);
        }

        foreach (var mirror in others)
        {
            ImGui.PushID(mirror.LocalScreenId);
            var owner = session.Members.FirstOrDefault(member => member.Id == mirror.Owner);
            var label = $"{(mirror.State!.Name.Length > 0 ? mirror.State.Name : "Screen")} ({owner?.Name ?? "someone"})";
            if (session.Dismissed.Contains(mirror.LocalScreenId))
                label += "  ·  removed by you";
            Ui.RowText(label, CrystalCastUiTheme.Text, f => f.Body);
            if (session.IsModerator && owner != null && CanModerate(session.Role, owner.Role))
            {
                ImGui.SameLine(0.0f, S(8.0f));
                if (Ui.Button("Hide for everyone"))
                    rooms.SetScreenHidden(session.Code, mirror.Owner, mirror.ScreenId, hide: true);
            }

            ImGui.PopID();
        }

        if (session.Dismissed.Count > 0 && Ui.Button("Bring back screens I removed"))
            rooms.RestoreDismissed(session.Code);

        if (session.IsModerator)
        {
            foreach (var key in hidden)
            {
                var at = key.IndexOf(':');
                var ownerId = key[..at];
                if (ownerId == session.You)
                    continue;

                ImGui.PushID(key);
                var owner = session.Members.FirstOrDefault(member => member.Id == ownerId);
                Ui.RowText($"A screen from {owner?.Name ?? "someone"}  ·  hidden for everyone", TextTertiary, f => f.Body);
                ImGui.SameLine(0.0f, S(8.0f));
                if (Ui.Button("Show again"))
                    rooms.SetScreenHidden(session.Code, ownerId, key[(at + 1)..], hide: false);
                ImGui.PopID();
            }
        }

        Ui.VerticalSpace(8.0f);
    }

    private void DrawPersistentMembers(PersistentSession session)
    {
        if (session.Members.Count == 0)
            return;

        var online = session.Members.Count(member => member.Online);
        Ui.RowText($"{session.Members.Count} members, {online} online", TextTertiary, f => f.Small);
        foreach (var member in session.Members.Take(MaxListedMembers * 2))
        {
            ImGui.PushID(member.Id);
            var color = member.You ? Accent : member.Online ? CrystalCastUiTheme.Text : TextTertiary;
            Ui.RowText(member.Name, color, f => f.Body);
            var tags = PersistentMemberTags(member);
            if (tags.Length > 0)
            {
                ImGui.SameLine(0.0f, S(6.0f));
                Ui.RowText(tags, TextTertiary, f => f.Small);
            }

            if (!member.You && session.IsModerator && CanModerate(session.Role, member.Role) || !member.You && session.IsOwner)
            {
                ImGui.SameLine(0.0f, S(6.0f));
                if (Ui.IconButton("##MemberActions", FontAwesomeIcon.EllipsisH, "Actions"))
                {
                    menuMemberId = member.Id;
                    confirming = string.Empty;
                    Ui.OpenMenu("PersistentMember");
                }

                if (menuMemberId == member.Id && Ui.BeginMenu("PersistentMember", ImGui.GetItemRectMax()))
                {
                    DrawMemberMenu(session, member);
                    Ui.EndMenu();
                }
            }

            ImGui.PopID();
        }

        Ui.VerticalSpace(8.0f);
    }

    private void DrawMemberMenu(PersistentSession session, RoomMember member)
    {
        if (session.IsOwner)
        {
            if (member.Role == "member" && Ui.MenuItem("Make moderator", icon: FontAwesomeIcon.UserShield))
                rooms.Moderate(session.Code, "promote", member.Id);
            if (member.Role == "mod" && Ui.MenuItem("Remove moderator", icon: FontAwesomeIcon.User))
                rooms.Moderate(session.Code, "demote", member.Id);
            if (Ui.MenuItem("Make owner", icon: FontAwesomeIcon.Crown))
                rooms.Moderate(session.Code, "transfer", member.Id);
            Ui.MenuSeparator();
        }

        if (CanModerate(session.Role, member.Role))
        {
            if (Ui.MenuItem("Kick", icon: FontAwesomeIcon.UserTimes))
                rooms.Moderate(session.Code, "kick", member.Id);
            if (Ui.MenuItem("Ban", danger: true, icon: FontAwesomeIcon.Ban))
                rooms.Moderate(session.Code, "ban", member.Id);
            Ui.MenuHint("Kicked people can come back with the password; banned people can't.");
        }
    }

    private void DrawPersistentBans(PersistentSession session)
    {
        if (!session.IsModerator || session.Bans.Count == 0)
            return;

        Ui.RowText("Banned", TextTertiary, f => f.Small);
        foreach (var ban in session.Bans)
        {
            ImGui.PushID(ban.Id);
            Ui.RowText(ban.Name, TextSecondary, f => f.Body);
            ImGui.SameLine(0.0f, S(8.0f));
            if (Ui.Button("Unban"))
                rooms.Moderate(session.Code, "unban", ban.Id);
            ImGui.PopID();
        }

        Ui.VerticalSpace(8.0f);
    }

    private void DrawOwnerControls(PersistentSession session)
    {
        if (!session.IsOwner || session.Connection == null)
            return;

        var width = Math.Max(S(120.0f), ImGui.GetContentRegionAvail().X - Ui.ButtonWidth("Set password") - S(8.0f));
        Ui.TextField("##RoomNameDraft", ref roomNameDraft, 64, session.RoomName.Length > 0 ? session.RoomName : "Room name", width);
        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Rename", disabled: roomNameDraft.Trim().Length == 0))
        {
            rooms.RenamePersistent(session.Code, roomNameDraft.Trim());
            roomNameDraft = string.Empty;
        }

        Ui.TextField("##PasswordDraft", ref passwordDraft, 128, session.PasswordSet ? "New password" : "Password", width, flags: ImGuiInputTextFlags.Password);
        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Set password", disabled: passwordDraft.Length == 0))
        {
            rooms.SetPersistentPassword(session.Code, passwordDraft);
            passwordDraft = string.Empty;
        }

        if (session.PasswordSet)
        {
            if (Ui.Button("Remove password", tooltip: "Anyone with the code can join."))
                rooms.SetPersistentPassword(session.Code, string.Empty);
            ImGui.SameLine(0.0f, S(8.0f));
        }

        Ui.Meta(session.PasswordSet
            ? "The room has a password. People already in it don't need it again."
            : "No password: anyone with the code can join.");
        Ui.VerticalSpace(8.0f);
    }

    private void DrawLeave(PersistentSession session)
    {
        var leaveKey = $"leave:{session.Code}";
        var leaveLabel = confirming == leaveKey ? "Click again to leave" : "Leave room";
        if (Ui.Button(leaveLabel, tooltip: session.IsOwner ? "The room goes to a moderator, or the longest member, and is deleted if nobody is left." : "You can come back with the code and password."))
        {
            if (confirming == leaveKey)
            {
                rooms.LeavePersistent(session.Code);
                confirming = string.Empty;
            }
            else
            {
                confirming = leaveKey;
            }
        }

        if (!session.IsOwner || session.Connection == null)
            return;

        ImGui.SameLine(0.0f, S(8.0f));
        var deleteKey = $"delete:{session.Code}";
        if (Ui.Button(confirming == deleteKey ? "Click again to delete for everyone" : "Delete room", textColor: CrystalCastUiTheme.Error))
        {
            if (confirming == deleteKey)
            {
                rooms.DeletePersistent(session.Code);
                confirming = string.Empty;
            }
            else
            {
                confirming = deleteKey;
            }
        }
    }

    private static string DescribePersistent(PersistentSession session)
    {
        if (session.Error.Length > 0)
            return session.Error;

        if (session.Connection == null)
            return "Not connected.";

        return session.Connection.Status switch
        {
            RoomConnectionStatus.Connecting => "Connecting…",
            RoomConnectionStatus.Reconnecting => $"Reconnecting… {session.Connection.LastError}",
            _ when !session.Welcomed => "Connecting…",
            _ => DescribeSharing(session),
        };
    }

    private static string DescribeSharing(PersistentSession session)
    {
        var mine = session.Published.Count;
        var others = session.Mirrors.Count;
        var sharing = mine switch
        {
            0 => "You're not sharing a screen",
            1 => "You share 1 screen",
            _ => $"You share {mine} screens",
        };
        return others switch
        {
            0 => $"Connected. {sharing}.",
            1 => $"Connected. {sharing}; 1 screen from others.",
            _ => $"Connected. {sharing}; {others} screens from others.",
        };
    }

    internal static string RoleLabel(string role) => role switch
    {
        "owner" => "Owner",
        "mod" => "Moderator",
        _ => "Member",
    };

    internal static string PersistentMemberTags(RoomMember member)
    {
        var parts = new List<string>();
        if (member.Role is "owner" or "mod")
            parts.Add(RoleLabel(member.Role));
        if (member.You)
            parts.Add("you");
        else if (!member.Online)
            parts.Add("offline");
        return parts.Count == 0 ? string.Empty : $"({string.Join(", ", parts)})";
    }

    /// <summary>Same rule as the relay: owners act on anyone else, moderators on plain members.</summary>
    internal static bool CanModerate(string actorRole, string targetRole) =>
        Rank(actorRole) >= 2 && Rank(actorRole) > Rank(targetRole);

    private static int Rank(string role) => role switch
    {
        "owner" => 3,
        "mod" => 2,
        _ => 1,
    };
}
