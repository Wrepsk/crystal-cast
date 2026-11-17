using System.Numerics;
using CrystalCast.Twitch;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

/// <summary>The button at the right of the chat's header.</summary>
internal enum TwitchChatHeaderButton
{
    None,
    /// <summary>Opens the chat in its own window (shown in the Twitch tab).</summary>
    PopOut,
    /// <summary>Puts the chat back in the Twitch tab (shown in the chat window).</summary>
    PopIn,
}

/// <summary>
/// A Twitch channel's live chat: the status line, the messages with badges and emotes, and the message box
/// when logged in. One chat is shared by the Twitch tab and the chat window, so both show the same messages
/// and only one connection is open. Reading chat needs no account.
/// </summary>
internal sealed partial class TwitchChatView(TwitchAccount account, ITextureProvider? textures) : IDisposable
{
    internal const float MinimumChatHeight = 220.0f;
    private const long BadgeRetryMs = 300_000;
    private const int MaxMessageLength = 500;

    private readonly TwitchChatConnection connection = new(account.GetChatLoginAsync);
    private readonly TwitchChatLog log = new();
    private readonly TwitchImageCache images = new(textures);
    private readonly TwitchBadgeCatalog badges = new();
    private readonly CancellationTokenSource cancel = new();
    private Task<IReadOnlyList<(TwitchBadge Badge, TwitchBadgeImage Image)>>? globalBadgesTask;
    private Task<IReadOnlyList<(TwitchBadge Badge, TwitchBadgeImage Image)>>? channelBadgesTask;
    private string channelBadgesFor = string.Empty;
    private long globalBadgesRetryTick;
    private readonly Dictionary<TwitchChatLine, (float Width, bool Deleted, float Height)> heights = new(ReferenceEqualityComparer.Instance);
    private long seenGeneration;
    private long seenVersion;
    private bool stickToBottom = true;
    private bool scrollToBottom;
    private float lastScrollY;

    private string messageDraft = string.Empty;
    private Task<TwitchSendResult>? sending;
    private string sendError = string.Empty;
    private bool refocusComposer;

    public void Dispose()
    {
        cancel.Cancel();
        connection.Dispose();
        images.Dispose();
        cancel.Dispose();
    }

    /// <summary>
    /// Draws the chat of <paramref name="channel"/>. With <paramref name="chatHeight"/> 0 the messages fill the
    /// space left above the message box. Returns true when the header button was pressed.
    /// </summary>
    public bool Draw(string channel, TwitchChatHeaderButton button, float chatHeight = 0.0f)
    {
        UpdateChat(channel);
        UpdateBadges();
        var pressed = DrawHeader(button);
        Ui.VerticalSpace(10.0f);
        var footer = ComposerHeight();
        var height = chatHeight > 0.0f
            ? chatHeight
            : Math.Max(S(MinimumChatHeight), ImGui.GetContentRegionAvail().Y - footer - S(8.0f) - ImGui.GetStyle().ItemSpacing.Y);
        DrawChat(height);
        Ui.VerticalSpace(8.0f);
        DrawComposer(channel);
        return pressed;
    }

    /// <summary>Closes the chat connection now, for a screen that was turned off. Its messages stay.</summary>
    public void Release() => connection.Release();

    /// <summary>
    /// Whether a screen's chat should be open: only while the plugin and that screen are on, so a Twitch link
    /// on a switched-off screen doesn't keep a connection to Twitch.
    /// </summary>
    internal static bool ShouldConnect(Configuration configuration, BrowserScreenProfile screen) =>
        configuration.Enabled && screen.Enabled;

    /// <summary>The note shown instead of the chat while <see cref="ShouldConnect"/> is false.</summary>
    internal static string OffNote(Configuration configuration) => configuration.Enabled
        ? "This screen is off. Turn it on to see its chat."
        : "CrystalCast is off. Turn it on to see the chat.";

    private void UpdateChat(string channel)
    {
        if (log.Channel != channel.ToLowerInvariant())
        {
            log.Reset(channel);
            heights.Clear();
            stickToBottom = true;
            scrollToBottom = true;
        }

        connection.Want(channel, account.ChatIdentity);
        var generation = connection.ConnectionGeneration;
        if (generation != seenGeneration)
        {
            seenGeneration = generation;
            log.MarkDisconnected();
        }

        // A busy channel can send hundreds of lines at once; only the last ones are kept anyway.
        for (var i = 0; i < 1000 && connection.TryDequeue(out var message); i++)
            log.Apply(message);
    }

    private bool DrawHeader(TwitchChatHeaderButton button)
    {
        var pressed = false;
        if (button != TwitchChatHeaderButton.None)
        {
            // Right-aligned on the status line's row; the cursor goes back for the status line.
            var rowStart = ImGui.GetCursorScreenPos();
            float lineHeight;
            using (Ui.Font(f => f.Small))
                lineHeight = ImGui.GetTextLineHeight();
            ImGui.SetCursorScreenPos(new Vector2(
                rowStart.X + ImGui.GetContentRegionAvail().X - Ui.FrameHeight,
                rowStart.Y + ((lineHeight - Ui.FrameHeight) * 0.5f)));
            pressed = button == TwitchChatHeaderButton.PopOut
                ? Ui.IconButton("##TwitchChatPopOut", FontAwesomeIcon.ExternalLinkAlt, "Open the chat in its own window")
                : Ui.IconButton("##TwitchChatPopIn", FontAwesomeIcon.CompressAlt, "Put the chat back in the Twitch tab");
            ImGui.SetCursorScreenPos(rowStart);
        }

        var (dot, word) = connection.Status switch
        {
            TwitchChatStatus.Connected when log.Joined => (Accent, "Chat"),
            TwitchChatStatus.Connected => (TextTertiary, "Joining"),
            TwitchChatStatus.Reconnecting => (Warning, "Reconnecting"),
            _ => (TextTertiary, "Connecting"),
        };

        var details = $"#{log.Channel}";
        if (connection.ConnectedAsUser)
            details += $" · as {account.DisplayName}";
        var modes = DescribeModes(log.RoomState);
        if (modes.Length > 0)
            details += $" · {modes}";
        Ui.StatusLine(dot, word, details);

        if (connection.Status == TwitchChatStatus.Reconnecting && !string.IsNullOrWhiteSpace(connection.LastError))
            Ui.Meta(connection.LastError);
        if (connection.LoginRefused)
            Ui.Meta("Twitch's chat didn't accept your login, so chat is read as a guest and you don't show in its list of who's here.");
        return pressed;
    }

    internal static string DescribeModes(TwitchRoomState state)
    {
        var modes = new List<string>();
        if (state.EmoteOnly == true)
            modes.Add("Emote-only");
        if (state.SubsOnly == true)
            modes.Add("Subscribers-only");
        if (state.FollowersOnlyMinutes is { } minutes)
            modes.Add(minutes == 0 ? "Followers-only" : $"Followers-only ({minutes} min)");
        if (state.SlowSeconds is > 0 and var slow)
            modes.Add($"Slow mode ({slow}s)");
        return string.Join(" · ", modes);
    }

    private void DrawChat(float height)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var min = ImGui.GetCursorScreenPos();
        var outer = ImGui.GetWindowDrawList();
        outer.AddRectFilled(min, min + new Vector2(width, height), U32(Inset), S(6.0f));
        outer.AddRect(min, min + new Vector2(width, height), U32(Hairline), S(6.0f), ImDrawFlags.None, 1.0f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, S(12.0f, 10.0f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        var open = ImGui.BeginChild("##TwitchChat", new Vector2(width, height), false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (open)
            DrawLines();

        ImGui.EndChild();
    }

    private void DrawLines()
    {
        var lineWidth = ImGui.GetContentRegionAvail().X;
        var gap = S(6.0f);
        using (Ui.Font(f => f.Body))
        {
            if (log.Lines.Count == 0)
            {
                Ui.Meta(log.Joined ? "No messages yet." : "Waiting for chat…");
            }

            var dl = ImGui.GetWindowDrawList();
            var scrollTop = ImGui.GetScrollY();
            var viewHeight = ImGui.GetWindowHeight();
            foreach (var line in log.Lines)
            {
                var height = MeasureLine(line, lineWidth);
                var start = ImGui.GetCursorScreenPos();
                var localY = ImGui.GetCursorPosY();
                // Only lines in view are drawn; the rest just take their space.
                if (localY + height >= scrollTop && localY <= scrollTop + viewHeight)
                    DrawLine(dl, line, start, lineWidth);

                ImGui.Dummy(new Vector2(lineWidth, height));
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + gap);
            }

            // Follow new lines while the view is at the bottom; scrolling up pauses that, like Twitch does.
            // New lines can leave the view short of the bottom for a frame, so only the user's own scrolling up
            // (the wheel, or dragging the scrollbar) pauses it.
            var scrollY = ImGui.GetScrollY();
            var atBottom = scrollY >= ImGui.GetScrollMaxY() - S(4.0f);
            var wheeledUp = ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel > 0.0f;
            var draggedUp = ImGui.IsMouseDown(ImGuiMouseButton.Left) && ImGui.IsWindowFocused() && scrollY < lastScrollY - 1.0f;
            if (wheeledUp || draggedUp)
                stickToBottom = false;
            else if (atBottom)
                stickToBottom = true;
            lastScrollY = scrollY;

            var hasNew = log.Version != seenVersion;
            seenVersion = log.Version;
            if (scrollToBottom || (stickToBottom && hasNew))
            {
                ImGui.SetScrollHereY(1.0f);
                scrollToBottom = false;
            }

            if (!stickToBottom)
                DrawResumeButton();
        }

        if (heights.Count > TwitchChatLog.MaxLines * 2)
        {
            var current = log.Lines.ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var stale in heights.Keys.Where(key => !current.Contains(key)).ToArray())
                heights.Remove(stale);
        }
    }

    private void DrawResumeButton()
    {
        const string label = "Chat paused · jump to newest";
        var windowPos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        float buttonWidth;
        using (Ui.Font(f => f.Small))
            buttonWidth = ImGui.CalcTextSize(label).X + S(24.0f);
        var buttonHeight = S(26.0f);
        var pos = new Vector2(windowPos.X + ((size.X - buttonWidth) * 0.5f), windowPos.Y + size.Y - buttonHeight - S(10.0f));

        ImGui.SetCursorScreenPos(pos);
        var clicked = ImGui.InvisibleButton("##TwitchChatResume", new Vector2(buttonWidth, buttonHeight));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(buttonWidth, buttonHeight), U32(hovered ? ControlHover : Control), buttonHeight * 0.5f);
        using (Ui.Font(f => f.Small))
        {
            var textSize = ImGui.CalcTextSize(label);
            dl.AddText(pos + ((new Vector2(buttonWidth, buttonHeight) - textSize) * 0.5f), U32(CrystalCastUiTheme.Text), label);
        }

        if (clicked)
        {
            stickToBottom = true;
            scrollToBottom = true;
        }
    }

    // ---------------------------------------------------------------- line layout

    /// <summary>Emotes are drawn this many text lines tall, about as big as on Twitch.</summary>
    private const float EmoteScale = 1.6f;

    /// <summary>
    /// One thing on a chat line: a word in a color, or a picture (an emote or badge) of a fixed square size.
    /// <see cref="Gap"/> is the space before it, dropped when it starts a new row.
    /// </summary>
    private readonly record struct Piece(string Text, Vector4 Color, float Gap, string ImageUrl = "", float ImageSize = 0.0f, string Tooltip = "")
    {
        public bool IsImage => ImageSize > 0.0f;
    }

    private readonly record struct Placed(Piece Piece, int Row, float X);

    private float MeasureLine(TwitchChatLine line, float width)
    {
        if (heights.TryGetValue(line, out var cached) && MathF.Abs(cached.Width - width) < 0.5f && cached.Deleted == line.Deleted)
            return cached.Height;

        var height = Layout(BuildPieces(line), Vector2.Zero, width, null);
        if (line.Kind == TwitchChatLineKind.Event && line.EventText.Length > 0)
            height += Layout(TextPieces(line.EventText, Accent), Vector2.Zero, width - S(10.0f), null);

        heights[line] = (width, line.Deleted, height);
        return height;
    }

    private void DrawLine(ImDrawListPtr dl, TwitchChatLine line, Vector2 start, float width)
    {
        if (line.Kind == TwitchChatLineKind.Event)
        {
            var eventHeight = line.EventText.Length > 0
                ? Layout(TextPieces(line.EventText, Accent), start + new Vector2(S(10.0f), 0.0f), width - S(10.0f), dl)
                : 0.0f;
            var pieces = BuildPieces(line);
            var messageHeight = pieces.Count > 0 ? Layout(pieces, start + new Vector2(S(10.0f), eventHeight), width - S(10.0f), dl) : 0.0f;
            dl.AddRectFilled(start, start + new Vector2(S(3.0f), eventHeight + messageHeight), U32(Accent), S(1.5f));
            return;
        }

        Layout(BuildPieces(line), start, width, dl);
    }

    private List<Piece> BuildPieces(TwitchChatLine line)
    {
        var pieces = new List<Piece>();
        if (line.Kind == TwitchChatLineKind.Notice)
        {
            foreach (var segment in line.Segments)
                pieces.AddRange(TextPieces(segment.Text, TextTertiary));
            return pieces;
        }

        // An event with no message of its own is just Twitch's line.
        if (line.Kind == TwitchChatLineKind.Event && line.Segments.Count == 0)
            return pieces;

        var lineHeight = ImGui.GetTextLineHeight();
        var badgeSize = MathF.Round(lineHeight);
        foreach (var badge in line.Badges)
        {
            if (badges.Resolve(badge) is { } image)
                pieces.Add(new Piece(string.Empty, Vector4.One, pieces.Count > 0 ? S(3.0f) : 0.0f, image.Url, badgeSize, image.Title));
        }

        var nameColor = TwitchChatColors.NameColor(line.Login, line.Color);
        pieces.Add(new Piece(line.DisplayName, nameColor, pieces.Count > 0 ? S(4.0f) : 0.0f));
        if (line.Deleted)
        {
            pieces.AddRange(TextPieces(": ", CrystalCastUiTheme.Text));
            pieces.AddRange(TextPieces("<message deleted>", TextDisabled));
            return pieces;
        }

        pieces.AddRange(TextPieces(line.IsAction ? " " : ": ", CrystalCastUiTheme.Text));
        var textColor = line.IsAction ? nameColor : CrystalCastUiTheme.Text;
        var emoteSize = MathF.Round(lineHeight * EmoteScale);
        foreach (var segment in line.Segments)
        {
            if (segment.Kind == TwitchChatSegmentKind.Emote && segment.EmoteId.Length > 0)
                pieces.Add(new Piece(segment.Text, textColor, 0.0f, TwitchImageCache.EmoteUrl(segment.EmoteId), emoteSize, segment.Text));
            else
                pieces.AddRange(TextPieces(segment.Text, textColor));
        }

        return pieces;
    }

    /// <summary>
    /// Text split into words; a word after a space has the space's width as its gap. Trailing spaces become a
    /// bare gap, so they still separate the text from an emote after it.
    /// </summary>
    private static IEnumerable<Piece> TextPieces(string text, Vector4 color)
    {
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var words = text.Split(' ');
        var pendingGap = 0.0f;
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
                pendingGap += spaceWidth;
            if (words[i].Length == 0)
                continue;

            yield return new Piece(words[i], color, pendingGap);
            pendingGap = 0.0f;
        }

        if (pendingGap > 0.0f)
            yield return new Piece(string.Empty, color, pendingGap);
    }

    /// <summary>
    /// Lays pieces out in rows, wrapping at <paramref name="width"/>; words wider than a row break between
    /// letters. A row is as tall as its tallest picture, and everything in it is centered on that height. Draws
    /// when given a draw list. Returns the height used.
    /// </summary>
    private float Layout(IEnumerable<Piece> pieces, Vector2 origin, float width, ImDrawListPtr? dl)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var placed = new List<Placed>();
        var row = 0;
        var x = 0.0f;
        var carriedGap = 0.0f;
        foreach (var piece in pieces)
        {
            if (!piece.IsImage && piece.Text.Length == 0)
            {
                carriedGap += piece.Gap;
                continue;
            }

            var gap = x > 0.0f ? piece.Gap + carriedGap : 0.0f;
            carriedGap = 0.0f;
            var itemWidth = piece.IsImage ? piece.ImageSize : ImGui.CalcTextSize(piece.Text).X;
            if (x > 0.0f && x + gap + itemWidth > width)
            {
                row++;
                x = 0.0f;
                gap = 0.0f;
            }

            x += gap;
            if (piece.IsImage)
            {
                placed.Add(new Placed(piece, row, x));
                x += itemWidth;
                continue;
            }

            // A word longer than a whole row (links, spam) breaks wherever it has to.
            var word = piece.Text;
            while (itemWidth > width - x && word.Length > 1)
            {
                var fit = FitChars(word, width - x);
                if (fit == 0 && x > 0.0f)
                {
                    row++;
                    x = 0.0f;
                    continue;
                }

                fit = Math.Max(1, fit);
                placed.Add(new Placed(piece with { Text = word[..fit] }, row, x));
                word = word[fit..];
                itemWidth = ImGui.CalcTextSize(word).X;
                row++;
                x = 0.0f;
            }

            placed.Add(new Placed(piece with { Text = word }, row, x));
            x += itemWidth;
        }

        var rowHeights = new float[row + 1];
        Array.Fill(rowHeights, lineHeight);
        foreach (var item in placed)
        {
            if (item.Piece.IsImage)
                rowHeights[item.Row] = Math.Max(rowHeights[item.Row], item.Piece.ImageSize);
        }

        if (dl is { } drawList)
        {
            var rowTops = new float[rowHeights.Length];
            for (var i = 1; i < rowTops.Length; i++)
                rowTops[i] = rowTops[i - 1] + rowHeights[i - 1];

            foreach (var item in placed)
            {
                var itemHeight = item.Piece.IsImage ? item.Piece.ImageSize : lineHeight;
                var pos = origin + new Vector2(item.X, rowTops[item.Row] + MathF.Round((rowHeights[item.Row] - itemHeight) * 0.5f));
                if (item.Piece.IsImage)
                    DrawPicture(drawList, item.Piece, pos);
                else
                    drawList.AddText(pos, U32(item.Piece.Color), item.Piece.Text);
            }
        }

        return rowHeights.Sum();
    }

    /// <summary>An emote or badge. Nothing shows until it loads; hovering it names it.</summary>
    private void DrawPicture(ImDrawListPtr dl, Piece piece, Vector2 pos)
    {
        var max = pos + new Vector2(piece.ImageSize);
        if (images.Get(piece.ImageUrl) is { } image)
        {
            // Pictures keep their shape inside the square.
            var aspect = (float)image.Width / Math.Max(1, image.Height);
            var size = aspect >= 1.0f
                ? new Vector2(piece.ImageSize, piece.ImageSize / aspect)
                : new Vector2(piece.ImageSize * aspect, piece.ImageSize);
            var topLeft = pos + ((new Vector2(piece.ImageSize) - size) * 0.5f);
            dl.AddImage(image.Handle, topLeft, topLeft + size);
        }

        if (piece.Tooltip.Length > 0 && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(pos, max))
            ImGui.SetTooltip(piece.Tooltip);
    }

    private static int FitChars(string word, float width)
    {
        var fit = 0;
        while (fit < word.Length && ImGui.CalcTextSize(word[..(fit + 1)]).X <= width)
            fit++;
        // Don't split a surrogate pair.
        if (fit > 0 && fit < word.Length && char.IsHighSurrogate(word[fit - 1]))
            fit--;
        return fit;
    }

    // ---------------------------------------------------------------- badges

    /// <summary>
    /// Loads Twitch's full badge lists while logged in: the global list once, and the channel's own badges
    /// whenever the chat moves to another channel. Logged out, the built-in common badges are used.
    /// </summary>
    private void UpdateBadges()
    {
        if (globalBadgesTask is { IsCompleted: true } global)
        {
            globalBadgesTask = null;
            if (global.IsCompletedSuccessfully)
            {
                badges.SetGlobal(global.Result);
                heights.Clear();
            }
            else
            {
                globalBadgesRetryTick = Environment.TickCount64 + BadgeRetryMs;
            }
        }

        if (channelBadgesTask is { IsCompleted: true } own)
        {
            channelBadgesTask = null;
            if (own.IsCompletedSuccessfully && channelBadgesFor == log.RoomId)
            {
                badges.SetChannel(channelBadgesFor, own.Result);
                heights.Clear();
            }
        }

        if (account.State != TwitchAccountState.SignedIn)
            return;

        if (!badges.HasGlobal && globalBadgesTask == null && Environment.TickCount64 >= globalBadgesRetryTick)
            globalBadgesTask = account.GetBadgesAsync(null, cancel.Token);

        var roomId = log.RoomId;
        if (roomId.Length > 0 && badges.ChannelId != roomId && channelBadgesTask == null && channelBadgesFor != roomId)
        {
            channelBadgesFor = roomId;
            channelBadgesTask = account.GetBadgesAsync(roomId, cancel.Token);
        }
    }

    private float ComposerHeight()
    {
        var height = Ui.FrameHeight;
        if (sendError.Length > 0 || account.State != TwitchAccountState.SignedIn)
            height += S(22.0f);
        return height;
    }

    private void DrawComposer(string channel)
    {
        if (account.State != TwitchAccountState.SignedIn)
        {
            Ui.Meta(account.Configured ? "Log in to send messages." : "Read-only chat.");
            return;
        }

        if (sending is { IsCompleted: true } done)
        {
            sending = null;
            var result = done.IsCompletedSuccessfully ? done.Result : new TwitchSendResult(false, done.Exception?.GetBaseException().Message ?? "Not sent.");
            if (result.Sent)
            {
                messageDraft = string.Empty;
                sendError = string.Empty;
            }
            else
            {
                sendError = result.Error;
            }

            refocusComposer = true;
        }

        var busy = sending != null;
        var sendLabel = "Chat";
        var spacing = S(8.0f);
        var fieldWidth = ImGui.GetContentRegionAvail().X - Ui.ButtonWidth(sendLabel) - Ui.FrameHeight - (spacing * 2.0f);
        if (refocusComposer)
        {
            ImGui.SetKeyboardFocusHere();
            refocusComposer = false;
        }

        var draft = messageDraft;
        var pressedEnter = Ui.TextField(
            "##TwitchMessage",
            ref draft,
            MaxMessageLength,
            $"Send a message to #{channel}",
            fieldWidth,
            disabled: busy,
            flags: ImGuiInputTextFlags.EnterReturnsTrue);
        if (draft != messageDraft)
        {
            messageDraft = draft;
            sendError = string.Empty;
        }

        ImGui.SameLine(0.0f, spacing);
        DrawEmoteButton(disabled: busy);

        ImGui.SameLine(0.0f, spacing);
        var canSend = !busy && !string.IsNullOrWhiteSpace(messageDraft);
        if ((Ui.Button(sendLabel, ButtonKind.Primary, disabled: !canSend) || pressedEnter) && canSend)
            sending = account.SendChatMessageAsync(channel, messageDraft.Trim(), cancel.Token);

        if (sendError.Length > 0)
            Ui.ErrorText(sendError);
    }
}
