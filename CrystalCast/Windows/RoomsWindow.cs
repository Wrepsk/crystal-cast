using System.Numerics;
using CrystalCast.Rooms;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

/// <summary>Share your screens under a room code and join other people's rooms.</summary>
internal sealed partial class RoomsWindow : CrystalCastWindow, IDisposable
{
    private readonly RoomService rooms;
    private readonly Configuration configuration;
    private readonly Action openSettings;
    private string joinDraft = string.Empty;
    private string joinError = string.Empty;
    private string shareError = string.Empty;
    private string copiedCode = string.Empty;
    private long copiedAtTick;
    private int tab;
    private static readonly string[] TabLabels = ["Quick rooms", "Persistent rooms"];

    public RoomsWindow(RoomService rooms, Configuration configuration, Action openSettings)
        : base("Rooms###CrystalCastRooms", ImGuiWindowFlags.NoCollapse)
    {
        this.rooms = rooms;
        this.configuration = configuration;
        this.openSettings = openSettings;
        Size = new Vector2(560.0f, 520.0f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420.0f, 320.0f) };
        IsOpen = false;
    }

    public void Open()
    {
        IsOpen = true;
        BringToFront();
    }

    /// <summary>Starts sharing <paramref name="screen"/> and shows the window with its code.</summary>
    public void Share(BrowserScreenProfile screen)
    {
        shareError = rooms.Share(screen, out var error) ? string.Empty : error;
        tab = 0;
        Open();
    }

    public void Suspend() => IsOpen = false;

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!rooms.HasRelay)
        {
            if (Ui.Banner(
                    "RoomsNoRelay",
                    BannerKind.Warning,
                    "The relay address isn't valid",
                    "Fix or clear the relay address in Settings > Rooms to share or join screens.",
                    "Open settings"))
            {
                openSettings();
            }

            Ui.VerticalSpace(12.0f);
        }

        tab = Ui.Tabs("RoomsTabs", TabLabels, tab);
        Ui.VerticalSpace(12.0f);
        if (tab == 0)
        {
            DrawSharing();
            Ui.VerticalSpace(20.0f);
            DrawJoining();
        }
        else
        {
            DrawPersistent();
        }
    }

    private void DrawSharing()
    {
        Ui.SectionHeader("Sharing", "Anyone with the code sees the screen where you are. Only you control it.");
        if (shareError.Length > 0)
            Ui.Text(shareError, CrystalCastUiTheme.Error, f => f.Small, wrap: true);

        if (rooms.Hosted.Count == 0)
        {
            Ui.Meta("You're not sharing a screen. Open a screen's ... menu in the main window and pick Share this screen.");
            return;
        }

        foreach (var room in rooms.Hosted.ToList())
        {
            ImGui.PushID(room.ScreenId);
            var screen = configuration.FindBrowserScreen(room.ScreenId);
            Ui.RowText(screen?.Name ?? "Screen", CrystalCastUiTheme.Text, f => f.SemiBold);

            if (room.Code.Length > 0)
            {
                Ui.Text(RoomCode.Format(room.Code), Accent, f => f.Mono);
                ImGui.SameLine(0.0f, S(12.0f));
                if (Ui.Button(copiedCode == room.Code && Environment.TickCount64 - copiedAtTick < 2000 ? "Copied" : "Copy code"))
                {
                    ImGui.SetClipboardText(RoomCode.Format(room.Code));
                    copiedCode = room.Code;
                    copiedAtTick = Environment.TickCount64;
                }

                ImGui.SameLine(0.0f, S(8.0f));
            }

            if (Ui.Button("Stop sharing"))
                rooms.StopSharing(room.ScreenId);

            Ui.Meta(DescribeHost(room));
            DrawMembers(room.Members);
            Ui.VerticalSpace(10.0f);
            ImGui.PopID();
        }
    }

    private void DrawJoining()
    {
        Ui.SectionHeader("Join a room", "Enter a code someone shared with you. Their screen appears where they are.");
        var fieldWidth = Math.Max(S(120.0f), ImGui.GetContentRegionAvail().X - Ui.ButtonWidth("Join") - S(8.0f));
        var enter = Ui.TextField(
            "##RoomCode",
            ref joinDraft,
            32,
            "ABCD-2345",
            fieldWidth,
            error: joinError.Length > 0,
            flags: ImGuiInputTextFlags.EnterReturnsTrue,
            mono: true);
        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Join", ButtonKind.Primary, disabled: !rooms.HasRelay) || enter && rooms.HasRelay)
        {
            if (rooms.Join(joinDraft, out var error))
            {
                joinDraft = string.Empty;
                joinError = string.Empty;
            }
            else
            {
                joinError = error;
            }
        }

        if (joinError.Length > 0)
            Ui.Text(joinError, CrystalCastUiTheme.Error, f => f.Small, wrap: true);
        else if (rooms.LastLeftRoom is { } left && left.Error.Length > 0)
            Ui.Meta($"Left room {RoomCode.Format(left.Code)}: {left.Error}");

        Ui.VerticalSpace(8.0f);
        foreach (var room in rooms.Joined.ToList())
        {
            ImGui.PushID(room.Code);
            var name = room.State?.Name is { Length: > 0 } stateName ? stateName : "Shared screen";
            Ui.RowText($"{name}  ·  {RoomCode.Format(room.Code)}", CrystalCastUiTheme.Text, f => f.SemiBold);
            if (Ui.Button("Leave"))
                rooms.Leave(room.Code);

            Ui.Meta(DescribeViewer(room));
            DrawMembers(room.Members);
            Ui.VerticalSpace(10.0f);
            ImGui.PopID();
        }
    }

    /// <summary>Most names listed for one room; the rest are counted.</summary>
    private const int MaxListedMembers = 30;

    /// <summary>Who is in a room, the host first. Nothing when the relay hasn't sent a list (an older relay).</summary>
    private static void DrawMembers(IReadOnlyList<RoomMember> members)
    {
        if (members.Count == 0)
            return;

        Ui.VerticalSpace(6.0f);
        Ui.RowText(members.Count == 1 ? "1 person in this room" : $"{members.Count} people in this room", TextTertiary, f => f.Small);
        foreach (var member in members.Take(MaxListedMembers))
        {
            Ui.RowText(member.Name, member.You ? Accent : CrystalCastUiTheme.Text, f => f.Body);
            var tags = RoomMemberTags(member);
            if (tags.Length > 0)
            {
                ImGui.SameLine(0.0f, S(6.0f));
                Ui.RowText(tags, TextTertiary, f => f.Small);
            }
        }

        if (members.Count > MaxListedMembers)
            Ui.Meta($"and {members.Count - MaxListedMembers} more");
    }

    internal static string RoomMemberTags(RoomMember member) => (member.Host, member.You) switch
    {
        (true, true) => "(Host, you)",
        (true, false) => "(Host)",
        (false, true) => "(you)",
        _ => string.Empty,
    };

    private static string DescribeHost(HostedRoom room)
    {
        if (room.Error.Length > 0)
            return room.Error;

        if (room.Creating != null || room.Connection == null)
            return "Creating a room…";

        return room.Connection.Status switch
        {
            RoomConnectionStatus.Connected => room.Viewers switch
            {
                0 => "Live. Nobody has joined yet.",
                1 => "Live. 1 person is watching.",
                _ => $"Live. {room.Viewers} people are watching.",
            },
            RoomConnectionStatus.Reconnecting => $"Reconnecting to the relay… {room.Connection.LastError}",
            _ => "Connecting to the relay…",
        };
    }

    private static string DescribeViewer(JoinedRoom room)
    {
        if (room.Error.Length > 0)
            return room.Error;

        return room.Connection.Status switch
        {
            RoomConnectionStatus.Connecting => "Connecting…",
            RoomConnectionStatus.Reconnecting => $"Reconnecting… {room.Connection.LastError}",
            _ when room.State == null => "Waiting for the host to start playing.",
            _ when !room.HostConnected => "The host is offline. Showing their last state; you leave the room if they aren't back within 3 minutes.",
            _ when !room.WasInPlace => "The host's screen is somewhere else. It shows when you're in the same place.",
            _ => "Watching.",
        };
    }
}
