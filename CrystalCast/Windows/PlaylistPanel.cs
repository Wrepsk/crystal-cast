using System.Numerics;
using CrystalCast.Playlist;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

/// <summary>
/// The Playlist tab: videos that play after the current one ends. While an integration holds the source lock,
/// adding, playing, skipping, reordering and the play-order switches are locked, but the user can always remove
/// videos, clear the list and turn off other plugins' access.
/// </summary>
internal sealed class PlaylistPanel(ScreenPlaylistAdvancer advancer, ScreenStateIpc ipc, PlaylistMetadataService metadata)
{
    private const float LabelWidth = MainLabelWidth;
    private const float LabelGap = MainLabelGap;
    private const long ClearConfirmMs = 3000;

    private readonly Dictionary<string, PlaylistUiState> uiStates = new(StringComparer.Ordinal);

    public bool Draw(BrowserScreenProfile screen)
    {
        var uiState = GetUiState(screen.ScreenId);
        var locked = SourceControlUi.IsSourceControlsLocked(screen);
        var changed = DrawAddRow(screen, uiState, locked);
        changed |= DrawOptions(screen, locked);
        Ui.VerticalSpace(24.0f);
        changed |= DrawList(screen, uiState, locked);
        return changed;
    }

    public void ClearScreen(string screenId) => uiStates.Remove(screenId);

    private static bool DrawAddRow(BrowserScreenProfile screen, PlaylistUiState uiState, bool locked)
    {
        var changed = false;
        var draft = uiState.Draft;
        var full = screen.Playlist.Count >= ScreenPlaylistPolicy.MaxEntries;

        Ui.RowLabel("Add video", LabelWidth, LabelGap);
        var spacing = S(8.0f);
        var fieldWidth = ImGui.GetContentRegionAvail().X - Ui.ButtonWidth("Add") - spacing;
        var pressedEnter = Ui.TextField(
            "##PlaylistLink",
            ref draft,
            1024,
            "Paste a YouTube, Twitch, Dailymotion, Vimeo or web link",
            Math.Max(S(80.0f), fieldWidth),
            error: uiState.Error.Length > 0,
            disabled: locked || full,
            flags: ImGuiInputTextFlags.EnterReturnsTrue);
        if (!string.Equals(draft, uiState.Draft, StringComparison.Ordinal))
        {
            uiState.Draft = draft;
            uiState.Error = string.Empty;
        }

        var valid = BrowserSourceDetector.TryDetect(draft, screen.ProviderKind, out var detected);
        ImGui.SameLine(0.0f, spacing);
        if ((Ui.Button("Add##PlaylistAdd", ButtonKind.Primary, disabled: locked || full || !valid) || pressedEnter)
            && !locked
            && !full)
        {
            if (ScreenPlaylistPolicy.TryCreateEntry(draft, screen.ProviderKind, addedByIpc: false, string.Empty, null, out var entry, out var error)
                && ScreenPlaylistPolicy.TryAdd(screen, entry, out error))
            {
                uiState.Draft = string.Empty;
                uiState.Error = string.Empty;
                changed = true;
            }
            else
            {
                uiState.Error = error;
            }
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        if (locked)
            Ui.Meta($"Locked by {SourceControlUi.LockedOwner(screen)}.");
        else if (full)
            Ui.Meta($"The playlist is full ({ScreenPlaylistPolicy.MaxEntries} videos).");
        else if (uiState.Error.Length > 0)
            Ui.ErrorText(uiState.Error);
        else if (valid && detected.IsGenericWebFallback)
            Ui.Text($"Not a supported video site · plays as a web page ({detected.Source.DisplayName})", Warning, f => f.Small, wrap: true);
        else if (valid)
            Ui.Meta($"{detected.Source.DisplayName} · press Add to queue it");
        else
            Ui.Meta("Videos play in order after the current one ends.");

        return changed;
    }

    private static bool DrawOptions(BrowserScreenProfile screen, bool locked)
    {
        var changed = false;
        var lockTooltip = locked ? $"Locked by {SourceControlUi.LockedOwner(screen)}" : null;

        var shuffle = screen.PlaylistShuffle;
        Ui.RowLabel("Shuffle", LabelWidth, LabelGap);
        if (Ui.Switch("##PlaylistShuffle", ref shuffle, locked, lockTooltip))
        {
            screen.PlaylistShuffle = shuffle;
            changed = true;
        }

        var loop = screen.PlaylistLoop;
        Ui.RowLabel("Loop playlist", LabelWidth, LabelGap);
        if (Ui.Switch("##PlaylistLoop", ref loop, locked, lockTooltip))
        {
            screen.PlaylistLoop = loop;
            changed = true;
        }

        if (BrowserSourceProviderRegistry.GetCapabilities(screen.ProviderKind).SupportsLoop
            && ScreenPlaylistPolicy.OverridesVideoLoop(screen)
            && IsVideoLoopOn(screen))
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta("The Source tab's loop setting is paused so the next video can start.");
        }

        // Always available, even on a locked screen: this switch is the user's protection.
        var allowIpc = screen.PlaylistAllowIpcAdds;
        Ui.RowLabel("Other plugins", LabelWidth, LabelGap);
        if (Ui.SwitchLabeled("##PlaylistAllowIpc", "Let other plugins add videos", ref allowIpc))
        {
            screen.PlaylistAllowIpcAdds = allowIpc;
            changed = true;
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(allowIpc
            ? "Plugins can add videos to this screen. Web pages they add still ask before opening."
            : "Only you can add videos, unless a plugin holds this screen's source lock.");
        return changed;
    }

    private bool DrawList(BrowserScreenProfile screen, PlaylistUiState uiState, bool locked)
    {
        var changed = false;
        var playlist = screen.Playlist;
        var lockTooltip = $"Locked by {SourceControlUi.LockedOwner(screen)}";

        Ui.RowText("Up next", CrystalCastUiTheme.Text, f => f.SemiBold);
        ImGui.SameLine(0.0f, S(8.0f));
        Ui.RowText($"{playlist.Count} of {ScreenPlaylistPolicy.MaxEntries}", TextTertiary, f => f.Small);

        var hasNext = ScreenPlaylistPolicy.HasNext(screen);
        var clearArmed = Environment.TickCount64 - uiState.ClearArmedAtTick < ClearConfirmMs;
        var clearLabel = clearArmed ? "Confirm clear##PlaylistClear" : "Clear##PlaylistClear";
        var actionsWidth = Ui.ButtonWidth("Skip") + S(4.0f) + Ui.ButtonWidth(clearArmed ? "Confirm clear" : "Clear");
        ImGui.SameLine(Math.Max(0.0f, ImGui.GetContentRegionMax().X - actionsWidth));
        if (Ui.Button("Skip##PlaylistSkip", ButtonKind.Ghost, disabled: locked || !hasNext,
                tooltip: locked ? lockTooltip : hasNext ? "Play the next video now" : "Nothing is left to play"))
        {
            if (advancer.TryAdvance(screen, out var advance))
            {
                ipc.PublishPlaylistAdvance(advance);
                changed = true;
            }
        }

        ImGui.SameLine(0.0f, S(4.0f));
        if (Ui.Button(clearLabel, ButtonKind.Ghost, disabled: playlist.Count == 0,
                textColor: clearArmed ? Error : null,
                tooltip: clearArmed ? "Click again to remove every video" : "Remove every video"))
        {
            if (clearArmed)
            {
                changed |= ScreenPlaylistPolicy.Clear(screen);
                uiState.ClearArmedAtTick = 0;
            }
            else
            {
                uiState.ClearArmedAtTick = Environment.TickCount64;
            }
        }

        Ui.VerticalSpace(8.0f);
        if (playlist.Count == 0)
        {
            Ui.Meta("No videos yet. Paste a link above and it plays after the current video ends.");
            return changed;
        }

        if (playlist.All(entry => entry.Played || ScreenPlaylistPolicy.IsCurrent(screen, entry)) && !screen.PlaylistLoop)
        {
            if (Ui.Button("Start over##PlaylistStartOver", ButtonKind.Secondary, disabled: locked,
                    tooltip: locked ? lockTooltip : "Mark every video unplayed so the playlist plays again"))
            {
                ScreenPlaylistPolicy.ResetPlayed(screen);
                changed = true;
            }

            ImGui.SameLine(0.0f, S(8.0f));
            Ui.RowText("Everything has played.", TextTertiary, f => f.Small);
            Ui.VerticalSpace(8.0f);
        }

        // Act after the loop so the list isn't changed while it is drawn.
        PendingAction? pending = null;
        for (var i = 0; i < playlist.Count; i++)
        {
            var action = DrawEntry(screen, playlist[i], i, playlist.Count, locked, lockTooltip);
            if (action != null)
                pending = action;
        }

        if (pending is { } act && ScreenPlaylistPolicy.FindEntry(screen, act.EntryId) is { } target)
        {
            switch (act.Kind)
            {
                case PendingKind.Play:
                    ipc.PublishPlaylistAdvance(advancer.Play(screen, target));
                    break;
                case PendingKind.MoveUp:
                    ScreenPlaylistPolicy.Move(screen, target.EntryId, ScreenPlaylistPolicy.IndexOf(screen, target.EntryId) - 1);
                    break;
                case PendingKind.MoveDown:
                    ScreenPlaylistPolicy.Move(screen, target.EntryId, ScreenPlaylistPolicy.IndexOf(screen, target.EntryId) + 1);
                    break;
                case PendingKind.Remove:
                    ScreenPlaylistPolicy.Remove(screen, target.EntryId);
                    break;
            }

            changed = true;
        }

        return changed;
    }

    private PendingAction? DrawEntry(BrowserScreenProfile screen, PlaylistEntry entry, int index, int count, bool locked, string lockTooltip)
    {
        PendingAction? action = null;
        var current = ScreenPlaylistPolicy.IsCurrent(screen, entry);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = S(58.0f);
        var dl = ImGui.GetWindowDrawList();

        if (current)
        {
            dl.AddRectFilled(start, start + new Vector2(width, height), U32(SelectedTint), S(4.0f));
            dl.AddRectFilled(start, start + new Vector2(S(3.0f), height), U32(Accent), S(2.0f));
        }

        // Position, or a marker for the playing and played entries.
        var markerWidth = S(24.0f);
        var markerCenter = start + new Vector2(S(6.0f) + (markerWidth * 0.5f), height * 0.5f);
        if (current)
        {
            Ui.DrawIcon(dl, markerCenter, FontAwesomeIcon.Play, Accent);
        }
        else if (entry.Played)
        {
            Ui.DrawIcon(dl, markerCenter, FontAwesomeIcon.Check, TextTertiary);
        }
        else
        {
            using (Ui.Font(f => f.Mono))
            {
                var number = (index + 1).ToString();
                var size = ImGui.CalcTextSize(number);
                dl.AddText(Ui.Snap(markerCenter - (size * 0.5f)), U32(TextSecondary), number);
            }
        }

        var thumbSize = S(80.0f, 45.0f);
        var thumbMin = new Vector2(start.X + S(6.0f) + markerWidth + S(4.0f), start.Y + ((height - thumbSize.Y) * 0.5f));
        DrawThumbnail(dl, entry, thumbMin, thumbMin + thumbSize, dimmed: entry.Played && !current);

        var buttonGap = S(2.0f);
        var buttonsWidth = (Ui.FrameHeight * 4.0f) + (buttonGap * 3.0f);
        var textX = thumbMin.X + thumbSize.X + S(10.0f);
        var textWidth = Math.Max(S(40.0f), start.X + width - buttonsWidth - S(8.0f) - textX);
        var title = ScreenPlaylistPolicy.DisplayTitle(entry);

        using (Ui.Font(f => f.Body))
        {
            var color = entry.Played && !current ? TextTertiary : CrystalCastUiTheme.Text;
            dl.AddText(new Vector2(textX, start.Y + S(11.0f)), U32(color), Ui.Ellipsize(title, textWidth));
        }

        using (Ui.Font(f => f.Small))
            dl.AddText(new Vector2(textX, start.Y + S(31.0f)), U32(TextTertiary), Ui.Ellipsize(DescribeEntry(entry, current), textWidth));

        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(thumbMin, new Vector2(textX + textWidth, start.Y + height)))
            ImGui.SetTooltip($"{title}\n{entry.Url}");

        ImGui.PushID(entry.EntryId);
        var buttonY = start.Y + ((height - Ui.FrameHeight) * 0.5f);
        var x = start.X + width - buttonsWidth;
        ImGui.SetCursorScreenPos(new Vector2(x, buttonY));
        if (Ui.IconButton("##Play", FontAwesomeIcon.Play, locked ? lockTooltip : current ? "Play from the start" : "Play now", disabled: locked))
            action = new PendingAction(PendingKind.Play, entry.EntryId);

        ImGui.SameLine(0.0f, buttonGap);
        if (Ui.IconButton("##Up", FontAwesomeIcon.ArrowUp, locked ? lockTooltip : "Move up", disabled: locked || index == 0))
            action = new PendingAction(PendingKind.MoveUp, entry.EntryId);

        ImGui.SameLine(0.0f, buttonGap);
        if (Ui.IconButton("##Down", FontAwesomeIcon.ArrowDown, locked ? lockTooltip : "Move down", disabled: locked || index == count - 1))
            action = new PendingAction(PendingKind.MoveDown, entry.EntryId);

        ImGui.SameLine(0.0f, buttonGap);
        if (Ui.IconButton("##Remove", FontAwesomeIcon.Times, current ? "Remove (the video keeps playing)" : "Remove"))
            action = new PendingAction(PendingKind.Remove, entry.EntryId);
        ImGui.PopID();

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(2.0f));
        return action;
    }

    /// <summary>The thumbnail cropped to 16:9 (which also trims YouTube's letterbox bars), or a placeholder.</summary>
    private void DrawThumbnail(ImDrawListPtr dl, PlaylistEntry entry, Vector2 min, Vector2 max, bool dimmed)
    {
        var rounding = S(3.0f);
        var image = metadata.GetThumbnail(entry);
        if (image == null || image.Width <= 0 || image.Height <= 0)
        {
            dl.AddRectFilled(min, max, U32(Inset), rounding);
            var icon = entry.ProviderKind == BrowserSourceProviderKind.GenericWeb ? FontAwesomeIcon.Globe : FontAwesomeIcon.Film;
            Ui.DrawIcon(dl, (min + max) * 0.5f, icon, CrystalCastUiTheme.TextDisabled);
            return;
        }

        var target = (max.X - min.X) / (max.Y - min.Y);
        var source = (float)image.Width / image.Height;
        Vector2 uv0, uv1;
        if (source > target)
        {
            var inset = (1.0f - (target / source)) * 0.5f;
            (uv0, uv1) = (new Vector2(inset, 0.0f), new Vector2(1.0f - inset, 1.0f));
        }
        else
        {
            var inset = (1.0f - (source / target)) * 0.5f;
            (uv0, uv1) = (new Vector2(0.0f, inset), new Vector2(1.0f, 1.0f - inset));
        }

        var tint = dimmed ? new Vector4(1.0f, 1.0f, 1.0f, 0.45f) : Vector4.One;
        dl.AddImageRounded(image.Handle, min, max, uv0, uv1, U32(tint), rounding);
    }

    /// <summary>"Playing · YouTube · channel · 3:33 · added by you", leaving out what isn't known.</summary>
    private string DescribeEntry(PlaylistEntry entry, bool current)
    {
        var parts = new List<string>(6);
        if (current)
            parts.Add("Playing");

        parts.Add(BrowserSourceControlsPanel.ProviderName(entry.ProviderKind));
        if (metadata.IsLive(entry) is { } live)
            parts.Add(live ? "Live" : "Offline");
        if (!string.IsNullOrWhiteSpace(entry.Author))
            parts.Add(entry.Author);
        if (entry.DurationSeconds > 0)
            parts.Add(SourceControlUi.FormatPlaybackPosition(entry.DurationSeconds * 1000L));

        parts.Add(entry.AddedByIpc
            ? $"added by {(string.IsNullOrWhiteSpace(entry.AddedBy) ? "a plugin" : entry.AddedBy)}"
            : "added by you");
        return string.Join(" · ", parts);
    }

    private static bool IsVideoLoopOn(BrowserScreenProfile screen)
    {
        return screen.ProviderKind switch
        {
            BrowserSourceProviderKind.YouTube => screen.LoopYouTube,
            BrowserSourceProviderKind.Dailymotion => screen.LoopDailymotion,
            BrowserSourceProviderKind.Vimeo => screen.LoopVimeo,
            BrowserSourceProviderKind.GenericWeb => screen.LoopGenericWeb,
            _ => false,
        };
    }

    private PlaylistUiState GetUiState(string screenId)
    {
        if (!uiStates.TryGetValue(screenId, out var state))
        {
            state = new PlaylistUiState();
            uiStates[screenId] = state;
        }

        return state;
    }

    private enum PendingKind
    {
        Play,
        MoveUp,
        MoveDown,
        Remove,
    }

    private readonly record struct PendingAction(PendingKind Kind, string EntryId);

    private sealed class PlaylistUiState
    {
        public string Draft { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public long ClearArmedAtTick { get; set; }
    }
}
