using System.Numerics;
using CrystalCast.Twitch;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

/// <summary>
/// The emote picker next to the message box: the emotes the user can send in this channel, grouped and
/// searchable. Picking one adds its name to the message; Twitch turns the name into the emote when it's sent.
/// </summary>
internal sealed partial class TwitchChatView
{
    private const string EmotePopupId = "##TwitchEmotePicker";
    private const float EmoteCellSize = 36.0f;
    private const float EmoteGridHeight = 260.0f;

    private Task<TwitchEmoteList>? emoteTask;
    private TwitchEmoteList? emoteList;
    /// <summary>The account and channel the loaded emotes are for; another one loads them again.</summary>
    private string emotesFor = string.Empty;
    private string emoteError = string.Empty;
    private string emoteSearch = string.Empty;
    private bool focusEmoteSearch;
    private bool emotePopupWasOpen;
    private IReadOnlyList<(TwitchEmoteGroup Group, IReadOnlyList<TwitchEmote> Emotes)> arrangedEmotes = [];
    private (TwitchEmoteList? List, string Search, string Channel) arrangedFor;

    /// <summary>The smiley button and its popup. Draw it on the message box's row.</summary>
    private void DrawEmoteButton(bool disabled)
    {
        UpdateEmotes();
        var buttonMin = ImGui.GetCursorScreenPos();
        if (Ui.IconButton("##TwitchEmotes", FontAwesomeIcon.Smile, "Emotes", disabled: disabled))
        {
            emoteSearch = string.Empty;
            focusEmoteSearch = true;
            LoadEmotesIfNeeded();
            ImGui.OpenPopup(EmotePopupId);
        }

        DrawEmotePopup(buttonMin);
    }

    private string EmoteKey => $"{account.UserId}|{log.RoomId}";

    private void LoadEmotesIfNeeded()
    {
        if (emoteTask != null || (emoteList != null && emotesFor == EmoteKey))
            return;

        emotesFor = EmoteKey;
        emoteError = string.Empty;
        emoteTask = account.GetEmotesAsync(log.RoomId, cancel.Token);
    }

    private void UpdateEmotes()
    {
        if (emoteTask is not { IsCompleted: true } done)
            return;

        emoteTask = null;
        if (done.IsCompletedSuccessfully)
            emoteList = done.Result;
        else if (!done.IsCanceled)
            emoteError = $"Couldn't load your emotes: {done.Exception?.GetBaseException().Message}";
    }

    private void DrawEmotePopup(Vector2 buttonMin)
    {
        var open = ImGui.IsPopupOpen(EmotePopupId);
        if (!open)
        {
            // Back to typing once the picker closes.
            if (emotePopupWasOpen)
                refocusComposer = true;
            emotePopupWasOpen = false;
            return;
        }

        emotePopupWasOpen = true;
        // Above the button, its right edge on the button's right edge.
        ImGui.SetNextWindowPos(new Vector2(buttonMin.X + Ui.FrameHeight, buttonMin.Y - S(6.0f)), ImGuiCond.Appearing, new Vector2(1.0f, 1.0f));
        ImGui.SetNextWindowSize(new Vector2(S(360.0f), 0.0f), ImGuiCond.Appearing);
        PushPopupStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, S(10.0f, 10.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, S(6.0f, 6.0f));
        if (ImGui.BeginPopup(EmotePopupId, ImGuiWindowFlags.NoMove))
        {
            DrawEmotePicker();
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar(2);
        PopPopupStyle();
    }

    private void DrawEmotePicker()
    {
        if (focusEmoteSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusEmoteSearch = false;
        }

        var search = emoteSearch;
        Ui.TextField("##TwitchEmoteSearch", ref search, 40, "Search emotes", ImGui.GetContentRegionAvail().X);
        emoteSearch = search;

        if (emoteTask != null && emoteList == null)
            Ui.Meta("Loading your emotes…");
        if (emoteError.Length > 0)
            Ui.ErrorText(emoteError);
        if (emoteList is { MissingScope: true })
            Ui.Meta("Showing Twitch's global emotes. Log out and log in again to also see your subscription and channel emotes.");

        if (emoteList == null)
            return;

        var groups = ArrangedEmotes(emoteList);
        if (groups.Count == 0)
        {
            Ui.Meta(emoteSearch.Trim().Length > 0 ? "No emotes match." : "No emotes.");
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        var gridOpen = ImGui.BeginChild("##TwitchEmoteGrid", new Vector2(0.0f, S(EmoteGridHeight)), false);
        ImGui.PopStyleColor();
        if (gridOpen)
        {
            foreach (var (group, emotes) in groups)
                DrawEmoteGroup(group, emotes);
        }

        ImGui.EndChild();
    }

    private IReadOnlyList<(TwitchEmoteGroup Group, IReadOnlyList<TwitchEmote> Emotes)> ArrangedEmotes(TwitchEmoteList list)
    {
        var key = (list, emoteSearch, log.RoomId);
        if (!ReferenceEquals(arrangedFor.List, list) || arrangedFor.Search != emoteSearch || arrangedFor.Channel != log.RoomId)
        {
            arrangedEmotes = TwitchEmotePicker.Arrange(list.Emotes, log.RoomId, emoteSearch);
            arrangedFor = key;
        }

        return arrangedEmotes;
    }

    private void DrawEmoteGroup(TwitchEmoteGroup group, IReadOnlyList<TwitchEmote> emotes)
    {
        Ui.Label($"{TwitchEmotePicker.Title(group)} · {emotes.Count}");
        var cell = S(EmoteCellSize);
        var width = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)(width / cell));
        var start = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var scrollTop = ImGui.GetScrollY();
        var viewBottom = scrollTop + ImGui.GetWindowHeight();
        var localTop = ImGui.GetCursorPosY();

        for (var i = 0; i < emotes.Count; i++)
        {
            var emote = emotes[i];
            var offset = new Vector2((i % columns) * cell, (i / columns) * cell);
            // Only emotes in view are drawn and loaded; the rest just take their space.
            var rowTop = localTop + offset.Y;
            if (rowTop + cell < scrollTop || rowTop > viewBottom)
                continue;

            var min = start + offset;
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##TwitchEmote{group}{emote.Id}", new Vector2(cell)))
            {
                messageDraft = TwitchEmotePicker.Insert(messageDraft, emote.Name);
                sendError = string.Empty;
            }

            if (ImGui.IsItemHovered())
            {
                dl.AddRectFilled(min, min + new Vector2(cell), U32(ControlHover), S(4.0f));
                Ui.Tooltip(emote.Name);
            }

            if (images.Get(TwitchImageCache.EmoteUrl(emote.Id)) is { } image)
            {
                var padding = S(4.0f);
                dl.AddImage(image.Handle, min + new Vector2(padding), min + new Vector2(cell - padding));
            }
        }

        var rows = (emotes.Count + columns - 1) / columns;
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, rows * cell));
    }
}
