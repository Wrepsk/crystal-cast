using System.Numerics;
using CrystalCast.Rooms;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

/// <summary>The Persistent rooms tab: your rooms as cards, then forms to join or create one.</summary>
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

    /// <summary>Right edge of the content inside the room card being drawn, for right-aligned controls.</summary>
    private float cardRight;

    private void DrawPersistent()
    {
        if (rooms.PersistentNotice.Length > 0)
            Ui.Text(rooms.PersistentNotice, CrystalCastUiTheme.Error, f => f.Small, wrap: true);

        Ui.SectionHeader("Your rooms", "Your screens show in every room you're connected to. Turn that off in Settings > Rooms, or per screen in its ... menu.");
        if (rooms.Persistent.Count == 0)
        {
            Ui.Meta("You're not in a persistent room yet. Join one with a code, or create your own below.");
        }
        else
        {
            foreach (var session in rooms.Persistent.ToList())
            {
                ImGui.PushID(session.Code);
                DrawPersistentRoom(session);
                ImGui.PopID();
            }
        }

        Ui.VerticalSpace(16.0f);
        DrawJoinPersistent();
        Ui.VerticalSpace(16.0f);
        DrawCreatePersistent();
        if (persistentError.Length > 0)
            Ui.Text(persistentError, CrystalCastUiTheme.Error, f => f.Small, wrap: true);
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

    // ---------------------------------------------------------------- room card

    private void DrawPersistentRoom(PersistentSession session)
    {
        cardRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X - S(16.0f);
        Ui.BeginCard();

        // A caret and the name, which open and close Manage; connect on the right.
        var expanded = expandedCode == session.Code;
        var name = session.RoomName.Length > 0 ? session.RoomName : "Persistent room";
        var connectLabel = session.Connection == null ? "Connect" : "Disconnect";
        var headerPos = ImGui.GetCursorScreenPos();
        var headerWidth = Math.Max(S(80.0f), cardRight - headerPos.X - Ui.ButtonWidth(connectLabel) - S(16.0f));
        if (ImGui.InvisibleButton("##RoomHeader", new Vector2(headerWidth, Ui.FrameHeight)))
            ToggleManage(session.Code, expanded);
        var headerHovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        Ui.DrawIcon(
            dl,
            new Vector2(headerPos.X + S(6.0f), headerPos.Y + (Ui.FrameHeight * 0.5f)),
            expanded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight,
            headerHovered || expanded ? Accent : TextSecondary);
        using (Ui.Font(f => f.SemiBold))
        {
            var label = Ui.Ellipsize(name, headerWidth - S(20.0f));
            var textY = headerPos.Y + ((Ui.FrameHeight - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(new Vector2(headerPos.X + S(20.0f), textY), U32(CrystalCastUiTheme.Text), label);
        }

        SameLineRight(Ui.ButtonWidth(connectLabel));
        if (session.Connection == null)
        {
            if (Ui.Button("Connect", ButtonKind.Primary))
                rooms.ConnectPersistent(session.Code);
        }
        else if (Ui.Button("Disconnect", tooltip: "Stop taking part for now. The room stays in your list."))
        {
            rooms.DisconnectPersistent(session.Code);
        }

        // Code with a copy button, and who's around.
        Ui.RowText(RoomCode.Format(session.Code), Accent, f => f.Mono);
        ImGui.SameLine(0.0f, S(2.0f));
        var copied = copiedCode == session.Code && Environment.TickCount64 - copiedAtTick < 2000;
        if (Ui.IconButton("##CopyCode", copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy, copied ? "Copied" : "Copy code", on: copied))
        {
            ImGui.SetClipboardText(RoomCode.Format(session.Code));
            copiedCode = session.Code;
            copiedAtTick = Environment.TickCount64;
        }

        if (session.Members.Count > 0)
        {
            ImGui.SameLine(0.0f, S(10.0f));
            var online = session.Members.Count(member => member.Online);
            Ui.RowText($"{online} of {session.Members.Count} online", TextTertiary, f => f.Small);
        }

        Ui.Text(DescribePersistent(session), session.Error.Length > 0 ? CrystalCastUiTheme.Error : TextSecondary, f => f.Small, wrap: true);
        if (session.RelayNotice.Length > 0)
            Ui.Text(session.RelayNotice, CrystalCastUiTheme.Warning, f => f.Small, wrap: true);

        Ui.VerticalSpace(4.0f);
        var autoJoin = configuration.PersistentRooms.FirstOrDefault(entry => entry.Code == session.Code)?.AutoJoin ?? true;
        if (Ui.SwitchLabeled("##AutoJoin", "Connect when the game starts", ref autoJoin))
            rooms.SetAutoJoin(session.Code, autoJoin);

        var manageLabel = expanded ? "Close" : "Manage";
        SameLineRight(Ui.ButtonWidth(manageLabel));
        if (Ui.Button(manageLabel, ButtonKind.Ghost, on: expanded))
            ToggleManage(session.Code, expanded);

        if (expanded)
        {
            DrawPersistentScreens(session);
            DrawPersistentMembers(session);
            DrawPersistentBans(session);
            DrawOwnerControls(session);
            DrawLeave(session);
        }

        Ui.EndCard();
    }

    private void DrawPersistentScreens(PersistentSession session)
    {
        CardSection("Screens");
        var mine = session.Published.Keys
            .Select(id => configuration.FindBrowserScreen(id))
            .Where(screen => screen != null)
            .ToList();
        var others = session.Mirrors.Values.Where(mirror => mirror.State != null).ToList();
        var hidden = session.IsModerator
            ? session.HiddenScreens.Where(key => !key.StartsWith(session.You + ":", StringComparison.Ordinal)).ToList()
            : [];
        if (mine.Count == 0 && others.Count == 0 && hidden.Count == 0)
        {
            Ui.Meta(session.Connection == null ? "Connect to see the screens in this room." : "Nobody is sharing a screen right now.");
            return;
        }

        foreach (var screen in mine)
        {
            Ui.RowText(screen!.Name, CrystalCastUiTheme.Text, f => f.Body);
            ImGui.SameLine(0.0f, S(8.0f));
            var note = session.IsHiddenForEveryone(session.You, screen.ScreenId) ? "yours, hidden by a moderator" : "yours";
            Ui.RowText(note, TextTertiary, f => f.Small);
        }

        foreach (var mirror in others)
        {
            ImGui.PushID(mirror.LocalScreenId);
            var owner = session.Members.FirstOrDefault(member => member.Id == mirror.Owner);
            Ui.RowText(mirror.State!.Name.Length > 0 ? mirror.State.Name : "Screen", CrystalCastUiTheme.Text, f => f.Body);
            ImGui.SameLine(0.0f, S(8.0f));
            var note = owner?.Name ?? "someone";
            if (session.Dismissed.Contains(mirror.LocalScreenId))
                note += ", removed by you";
            else if (!mirror.Shown && configuration.PersistentRoomsShowOthersScreens && !session.IsHiddenForEveryone(mirror.Owner, mirror.ScreenId))
                note += ", in another area";
            Ui.RowText(note, TextTertiary, f => f.Small);

            if (session.IsModerator && owner != null && CanModerate(session.Role, owner.Role))
            {
                SameLineRight(Ui.ButtonWidth("Hide for everyone"));
                if (Ui.Button("Hide for everyone", ButtonKind.Ghost))
                    rooms.SetScreenHidden(session.Code, mirror.Owner, mirror.ScreenId, hide: true);
            }

            ImGui.PopID();
        }

        foreach (var key in hidden)
        {
            var at = key.IndexOf(':');
            var ownerId = key[..at];
            ImGui.PushID(key);
            var owner = session.Members.FirstOrDefault(member => member.Id == ownerId);
            Ui.RowText($"A screen from {owner?.Name ?? "someone"}", TextTertiary, f => f.Body);
            ImGui.SameLine(0.0f, S(8.0f));
            Ui.RowText("hidden for everyone", TextTertiary, f => f.Small);
            SameLineRight(Ui.ButtonWidth("Show again"));
            if (Ui.Button("Show again", ButtonKind.Ghost))
                rooms.SetScreenHidden(session.Code, ownerId, key[(at + 1)..], hide: false);
            ImGui.PopID();
        }

        if (session.Dismissed.Count > 0 && Ui.Button("Bring back screens I removed", ButtonKind.Ghost))
            rooms.RestoreDismissed(session.Code);
    }

    private void DrawPersistentMembers(PersistentSession session)
    {
        if (session.Members.Count == 0)
            return;

        CardSection("Members");
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

            if (!member.You && (session.IsOwner || session.IsModerator && CanModerate(session.Role, member.Role)))
            {
                SameLineRight(Ui.FrameHeight);
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

        if (session.Members.Count > MaxListedMembers * 2)
            Ui.Meta($"and {session.Members.Count - (MaxListedMembers * 2)} more");
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
            Ui.MenuHint("Kicked people can come back; banned can't.");
        }
    }

    private void DrawPersistentBans(PersistentSession session)
    {
        if (!session.IsModerator || session.Bans.Count == 0)
            return;

        CardSection("Banned");
        foreach (var ban in session.Bans)
        {
            ImGui.PushID(ban.Id);
            Ui.RowText(ban.Name, TextSecondary, f => f.Body);
            SameLineRight(Ui.ButtonWidth("Unban"));
            if (Ui.Button("Unban", ButtonKind.Ghost))
                rooms.Moderate(session.Code, "unban", ban.Id);
            ImGui.PopID();
        }
    }

    private void DrawOwnerControls(PersistentSession session)
    {
        if (!session.IsOwner || session.Connection == null)
            return;

        CardSection("Room settings");
        var buttonWidth = Math.Max(Ui.ButtonWidth("Rename"), Ui.ButtonWidth("Set password"));
        var width = Math.Max(S(120.0f), cardRight - ImGui.GetCursorScreenPos().X - buttonWidth - S(8.0f));
        Ui.TextField("##RoomNameDraft", ref roomNameDraft, 64, session.RoomName.Length > 0 ? session.RoomName : "Room name", width);
        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Rename", width: buttonWidth, disabled: roomNameDraft.Trim().Length == 0))
        {
            rooms.RenamePersistent(session.Code, roomNameDraft.Trim());
            roomNameDraft = string.Empty;
        }

        Ui.TextField("##PasswordDraft", ref passwordDraft, 128, session.PasswordSet ? "New password" : "Password", width, flags: ImGuiInputTextFlags.Password);
        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Set password", width: buttonWidth, disabled: passwordDraft.Length == 0))
        {
            rooms.SetPersistentPassword(session.Code, passwordDraft);
            passwordDraft = string.Empty;
        }

        Ui.Meta(session.PasswordSet
            ? "The room has a password. People already in it don't need it again."
            : "No password: anyone with the code can join.");
        if (session.PasswordSet && Ui.Button("Remove password", ButtonKind.Ghost))
            rooms.SetPersistentPassword(session.Code, string.Empty);
    }

    private void DrawLeave(PersistentSession session)
    {
        CardSection(string.Empty);
        var leaveKey = $"leave:{session.Code}";
        var leaveLabel = confirming == leaveKey ? "Click again to leave" : "Leave room";
        if (Ui.Button(leaveLabel, tooltip: session.IsOwner ? "The room goes to a moderator, or else the longest member." : "You can come back with the code and password."))
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

    // ---------------------------------------------------------------- card helpers

    /// <summary>A hairline across the card, then a small section title.</summary>
    private void CardSection(string title)
    {
        Ui.VerticalSpace(10.0f);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(pos, new Vector2(cardRight, pos.Y + 1.0f), U32(Hairline));
        Ui.VerticalSpace(10.0f);
        if (title.Length > 0)
            Ui.Text(title, TextTertiary, f => f.Small);
    }

    /// <summary>Puts the next item on this row, flush with the card's right edge.</summary>
    private void SameLineRight(float itemWidth)
    {
        ImGui.SameLine();
        var pos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(Math.Max(pos.X, cardRight - itemWidth), pos.Y));
    }

    private void ToggleManage(string code, bool expanded)
    {
        expandedCode = expanded ? string.Empty : code;
        confirming = string.Empty;
        roomNameDraft = string.Empty;
        passwordDraft = string.Empty;
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
            0 => $"{sharing}.",
            1 => $"{sharing}; 1 screen from others.",
            _ => $"{sharing}; {others} screens from others.",
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
