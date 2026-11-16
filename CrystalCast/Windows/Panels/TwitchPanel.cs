using System.Numerics;
using CrystalCast.Sources;
using CrystalCast.Sources.Providers;
using CrystalCast.Twitch;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

/// <summary>
/// The Twitch tab, shown while the selected screen plays Twitch: the channel's live chat. Reading chat needs no
/// account. Only the chat of the selected screen is open, and the connection closes a minute after the tab is
/// left.
/// </summary>
internal sealed class TwitchPanel : ISourceTab
{
    private const float MinimumChatHeight = 220.0f;

    private readonly TwitchChatConnection connection = new();
    private readonly TwitchChatLog log = new();
    private readonly Dictionary<TwitchChatLine, (float Width, bool Deleted, float Height)> heights = new(ReferenceEqualityComparer.Instance);
    private long seenGeneration;
    private long seenVersion;
    private bool stickToBottom = true;
    private bool scrollToBottom;
    private float lastScrollY;

    public string Label => "Twitch";

    public bool AppliesTo(BrowserScreenProfile screen) => TryGetSource(screen, out _);

    public void Dispose() => connection.Dispose();

    public bool Draw(BrowserScreenProfile screen)
    {
        if (!TryGetSource(screen, out var source))
            return false;

        if (source.Kind != TwitchSourceKind.Channel)
        {
            Ui.SectionHeader("Chat", "This screen plays a Twitch video. Chat is shown for live channels.");
            return false;
        }

        UpdateChat(source.ChannelName);
        DrawHeader();
        Ui.VerticalSpace(10.0f);
        DrawChat();
        Ui.VerticalSpace(8.0f);
        Ui.Meta("Read-only. Logging in to send messages is coming next.");
        return false;
    }

    internal static bool TryGetSource(BrowserScreenProfile screen, out TwitchSourceReference source)
    {
        source = default;
        if (screen.ProviderKind != BrowserSourceProviderKind.Twitch
            || !BrowserSourceDescriptors.Twitch.TryParse(screen.TwitchUrl, out var parsed)
            || parsed is not TwitchSourceReference twitch)
        {
            return false;
        }

        source = twitch;
        return true;
    }

    private void UpdateChat(string channel)
    {
        if (log.Channel != channel.ToLowerInvariant())
        {
            log.Reset(channel);
            heights.Clear();
            stickToBottom = true;
            scrollToBottom = true;
        }

        connection.Want(channel);
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

    private void DrawHeader()
    {
        var (dot, word) = connection.Status switch
        {
            TwitchChatStatus.Connected when log.Joined => (Accent, "Chat"),
            TwitchChatStatus.Connected => (TextTertiary, "Joining"),
            TwitchChatStatus.Reconnecting => (Warning, "Reconnecting"),
            _ => (TextTertiary, "Connecting"),
        };

        var details = $"#{log.Channel}";
        var modes = DescribeModes(log.RoomState);
        if (modes.Length > 0)
            details += $" · {modes}";
        Ui.StatusLine(dot, word, details);

        if (connection.Status == TwitchChatStatus.Reconnecting && !string.IsNullOrWhiteSpace(connection.LastError))
            Ui.Meta(connection.LastError);
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

    private void DrawChat()
    {
        var height = Math.Max(S(MinimumChatHeight), ImGui.GetContentRegionAvail().Y - S(28.0f));
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

    private readonly record struct Run(string Text, Vector4 Color);

    private float MeasureLine(TwitchChatLine line, float width)
    {
        if (heights.TryGetValue(line, out var cached) && MathF.Abs(cached.Width - width) < 0.5f && cached.Deleted == line.Deleted)
            return cached.Height;

        var height = Layout(BuildRuns(line), Vector2.Zero, width, null);
        if (line.Kind == TwitchChatLineKind.Event && line.EventText.Length > 0)
            height += Layout([new Run(line.EventText, Accent)], Vector2.Zero, width - S(10.0f), null);

        heights[line] = (width, line.Deleted, height);
        return height;
    }

    private static void DrawLine(ImDrawListPtr dl, TwitchChatLine line, Vector2 start, float width)
    {
        if (line.Kind == TwitchChatLineKind.Event)
        {
            var eventHeight = line.EventText.Length > 0
                ? Layout([new Run(line.EventText, Accent)], start + new Vector2(S(10.0f), 0.0f), width - S(10.0f), dl)
                : 0.0f;
            var runs = BuildRuns(line);
            var messageHeight = runs.Count > 0 ? Layout(runs, start + new Vector2(S(10.0f), eventHeight), width - S(10.0f), dl) : 0.0f;
            dl.AddRectFilled(start, start + new Vector2(S(3.0f), eventHeight + messageHeight), U32(Accent), S(1.5f));
            return;
        }

        Layout(BuildRuns(line), start, width, dl);
    }

    private static List<Run> BuildRuns(TwitchChatLine line)
    {
        var runs = new List<Run>();
        if (line.Kind == TwitchChatLineKind.Notice)
        {
            foreach (var segment in line.Segments)
                runs.Add(new Run(segment.Text, TextTertiary));
            return runs;
        }

        // An event with no message of its own is just Twitch's line.
        if (line.Kind == TwitchChatLineKind.Event && line.Segments.Count == 0)
            return runs;

        var nameColor = TwitchChatColors.NameColor(line.Login, line.Color);
        runs.Add(new Run(line.DisplayName, nameColor));
        if (line.Deleted)
        {
            runs.Add(new Run(": ", CrystalCastUiTheme.Text));
            runs.Add(new Run("<message deleted>", TextDisabled));
            return runs;
        }

        runs.Add(new Run(line.IsAction ? " " : ": ", CrystalCastUiTheme.Text));
        var textColor = line.IsAction ? nameColor : CrystalCastUiTheme.Text;
        foreach (var segment in line.Segments)
            runs.Add(new Run(segment.Text, segment.Kind == TwitchChatSegmentKind.Emote ? AccentHover : textColor));
        return runs;
    }

    /// <summary>
    /// Lays runs out word by word, wrapping at <paramref name="width"/>; words wider than a line break between
    /// letters. Draws when given a draw list. Returns the height used.
    /// </summary>
    private static float Layout(IReadOnlyList<Run> runs, Vector2 origin, float width, ImDrawListPtr? dl)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var x = 0.0f;
        var y = 0.0f;
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        foreach (var run in runs)
        {
            var color = U32(run.Color);
            var words = run.Text.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                if (i > 0)
                    x += spaceWidth;

                var word = words[i];
                if (word.Length == 0)
                    continue;

                var wordWidth = ImGui.CalcTextSize(word).X;
                if (x > 0.0f && x + wordWidth > width)
                {
                    x = 0.0f;
                    y += lineHeight;
                }

                // A word longer than a whole line (links, spam) breaks wherever it has to.
                while (wordWidth > width && word.Length > 1)
                {
                    var fit = FitChars(word, width - x);
                    if (fit == 0 && x > 0.0f)
                    {
                        x = 0.0f;
                        y += lineHeight;
                        continue;
                    }

                    fit = Math.Max(1, fit);
                    dl?.AddText(origin + new Vector2(x, y), color, word[..fit]);
                    word = word[fit..];
                    wordWidth = ImGui.CalcTextSize(word).X;
                    x = 0.0f;
                    y += lineHeight;
                }

                dl?.AddText(origin + new Vector2(x, y), color, word);
                x += wordWidth;
            }
        }

        return y + lineHeight;
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
}
