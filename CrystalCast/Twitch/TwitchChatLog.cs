using System.Text;

namespace CrystalCast.Twitch;

internal enum TwitchChatSegmentKind
{
    Text,
    Emote,
}

/// <summary>A run of plain text, or one Twitch emote (its name in <see cref="Text"/>).</summary>
internal readonly record struct TwitchChatSegment(TwitchChatSegmentKind Kind, string Text, string EmoteId = "");

internal enum TwitchChatLineKind
{
    Message,
    /// <summary>Subs, gifted subs, raids and the like: Twitch's own text, with the viewer's message if any.</summary>
    Event,
    /// <summary>A notice from Twitch or a note from CrystalCast.</summary>
    Notice,
}

internal sealed class TwitchChatLine
{
    public required TwitchChatLineKind Kind { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Login { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>The viewer's name color as <c>#RRGGBB</c>, or empty when they never picked one.</summary>
    public string Color { get; init; } = string.Empty;
    public IReadOnlyList<TwitchBadge> Badges { get; init; } = [];
    public IReadOnlyList<TwitchChatSegment> Segments { get; init; } = [];
    /// <summary>For events: Twitch's description, such as "X subscribed for 3 months".</summary>
    public string EventText { get; init; } = string.Empty;
    /// <summary>A /me message, drawn in the name's color.</summary>
    public bool IsAction { get; init; }
    public bool Deleted { get; set; }
}

/// <summary>Chat modes Twitch reports for the channel. Null where Twitch hasn't said yet.</summary>
internal sealed record TwitchRoomState(bool? EmoteOnly, int? FollowersOnlyMinutes, int? SlowSeconds, bool? SubsOnly)
{
    public static readonly TwitchRoomState Unknown = new(null, null, null, null);
}

/// <summary>
/// The chat of one channel as the tab shows it. Fed parsed IRC lines on the game's main thread; keeps the
/// most recent <see cref="MaxLines"/> lines and applies deletions, timeouts and mode changes.
/// </summary>
internal sealed class TwitchChatLog
{
    public const int MaxLines = 300;

    private readonly List<TwitchChatLine> lines = [];

    public string Channel { get; private set; } = string.Empty;
    /// <summary>True once Twitch confirmed the join (its first ROOMSTATE for the channel).</summary>
    public bool Joined { get; private set; }
    public TwitchRoomState RoomState { get; private set; } = TwitchRoomState.Unknown;
    /// <summary>The channel's user ID, from Twitch's ROOMSTATE; used to load the channel's own badges.</summary>
    public string RoomId { get; private set; } = string.Empty;
    public IReadOnlyList<TwitchChatLine> Lines => lines;

    /// <summary>Counts every line ever added, so the view can tell new lines arrived after trimming.</summary>
    public long Version { get; private set; }

    public void Reset(string channel)
    {
        Channel = channel.ToLowerInvariant();
        Joined = false;
        RoomState = TwitchRoomState.Unknown;
        RoomId = string.Empty;
        lines.Clear();
        Version++;
    }

    /// <summary>Marks the log as no longer joined, e.g. after the connection dropped; lines stay.</summary>
    public void MarkDisconnected() => Joined = false;

    public void AddNotice(string text)
    {
        Add(new TwitchChatLine { Kind = TwitchChatLineKind.Notice, Segments = [new(TwitchChatSegmentKind.Text, text)] });
    }

    /// <summary>Applies one line from Twitch. Lines for other channels are ignored.</summary>
    public void Apply(TwitchIrcMessage message)
    {
        if (string.IsNullOrEmpty(Channel) || message.Channel != Channel)
            return;

        switch (message.Command)
        {
            case "PRIVMSG":
                Add(FromPrivmsg(message));
                break;
            case "USERNOTICE":
                Add(FromUserNotice(message));
                break;
            case "NOTICE":
                if (!string.IsNullOrWhiteSpace(message.Trailing))
                    AddNotice(message.Trailing);
                break;
            case "CLEARMSG":
                var targetId = message.Tag("target-msg-id");
                foreach (var line in lines)
                {
                    if (line.Id == targetId && !string.IsNullOrEmpty(targetId))
                        line.Deleted = true;
                }

                break;
            case "CLEARCHAT":
                ApplyClearChat(message);
                break;
            case "ROOMSTATE":
                Joined = true;
                RoomState = MergeRoomState(RoomState, message);
                if (message.Tag("room-id") is { Length: > 0 } roomId)
                    RoomId = roomId;
                break;
        }
    }

    private void ApplyClearChat(TwitchIrcMessage message)
    {
        var login = message.Trailing.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(login))
        {
            foreach (var line in lines)
            {
                if (line.Kind != TwitchChatLineKind.Notice)
                    line.Deleted = true;
            }

            AddNotice("A moderator cleared the chat.");
            return;
        }

        foreach (var line in lines)
        {
            if (line.Login == login)
                line.Deleted = true;
        }
    }

    private void Add(TwitchChatLine line)
    {
        lines.Add(line);
        if (lines.Count > MaxLines)
            lines.RemoveRange(0, lines.Count - MaxLines);
        Version++;
    }

    internal static TwitchRoomState MergeRoomState(TwitchRoomState state, TwitchIrcMessage message)
    {
        static int? Number(TwitchIrcMessage m, string tag) =>
            m.Tags.TryGetValue(tag, out var value) && int.TryParse(value, out var number) ? number : null;

        var followers = Number(message, "followers-only");
        return new TwitchRoomState(
            Number(message, "emote-only") is { } emoteOnly ? emoteOnly == 1 : state.EmoteOnly,
            // Twitch sends -1 when followers-only is off.
            followers is { } minutes ? (minutes < 0 ? null : minutes) : state.FollowersOnlyMinutes,
            Number(message, "slow") is { } slow ? slow : state.SlowSeconds,
            Number(message, "subs-only") is { } subsOnly ? subsOnly == 1 : state.SubsOnly);
    }

    internal static TwitchChatLine FromPrivmsg(TwitchIrcMessage message)
    {
        var text = message.Trailing;
        var isAction = false;
        const string actionStart = "\u0001ACTION ";
        if (text.StartsWith(actionStart, StringComparison.Ordinal))
        {
            isAction = true;
            text = text[actionStart.Length..].TrimEnd('\u0001');
        }

        var login = message.PrefixLogin.ToLowerInvariant();
        return new TwitchChatLine
        {
            Kind = TwitchChatLineKind.Message,
            Id = message.Tag("id"),
            Login = login,
            DisplayName = DisplayName(message.Tag("display-name"), login),
            Color = message.Tag("color"),
            Badges = ParseBadges(message.Tag("badges")),
            Segments = Segment(text, message.Tag("emotes")),
            IsAction = isAction,
        };
    }

    internal static TwitchChatLine FromUserNotice(TwitchIrcMessage message)
    {
        var login = message.Tag("login").ToLowerInvariant();
        return new TwitchChatLine
        {
            Kind = TwitchChatLineKind.Event,
            Id = message.Tag("id"),
            Login = login,
            DisplayName = DisplayName(message.Tag("display-name"), login),
            Color = message.Tag("color"),
            Badges = ParseBadges(message.Tag("badges")),
            EventText = message.Tag("system-msg").Trim(),
            Segments = Segment(message.Trailing, message.Tag("emotes")),
        };
    }

    /// <summary>
    /// The name to show. Twitch shows <c>Display (login)</c> when the display name is in another script, such as
    /// Japanese, and so doesn't read as the login.
    /// </summary>
    internal static string DisplayName(string displayName, string login)
    {
        displayName = displayName.Trim();
        if (string.IsNullOrEmpty(displayName))
            return login;

        return string.Equals(displayName, login, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(login)
            ? displayName
            : $"{displayName} ({login})";
    }

    /// <summary><c>broadcaster/1,subscriber/12</c> → the badges, in Twitch's order.</summary>
    internal static IReadOnlyList<TwitchBadge> ParseBadges(string badges)
    {
        if (string.IsNullOrWhiteSpace(badges))
            return [];

        return badges
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(badge => badge.Split('/', 2))
            .Where(parts => parts[0].Length > 0)
            .Select(parts => new TwitchBadge(parts[0], parts.Length > 1 ? parts[1] : string.Empty))
            .ToArray();
    }

    /// <summary>
    /// Splits a message into text and emotes. The emotes tag is <c>id:start-end,start-end/id:start-end</c>, with
    /// positions counted in Unicode code points, not UTF-16 chars, so emoji before an emote don't shift it.
    /// </summary>
    internal static IReadOnlyList<TwitchChatSegment> Segment(string text, string emotesTag)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var ranges = ParseEmoteRanges(emotesTag);
        if (ranges.Count == 0)
            return [new TwitchChatSegment(TwitchChatSegmentKind.Text, text)];

        var runes = text.EnumerateRunes().ToArray();
        var segments = new List<TwitchChatSegment>();
        var position = 0;
        foreach (var (start, end, id) in ranges)
        {
            if (start < position || end >= runes.Length || end < start)
                continue;

            if (start > position)
                segments.Add(new TwitchChatSegment(TwitchChatSegmentKind.Text, Join(runes, position, start)));

            segments.Add(new TwitchChatSegment(TwitchChatSegmentKind.Emote, Join(runes, start, end + 1), id));
            position = end + 1;
        }

        if (position < runes.Length)
            segments.Add(new TwitchChatSegment(TwitchChatSegmentKind.Text, Join(runes, position, runes.Length)));

        return segments;
    }

    private static List<(int Start, int End, string Id)> ParseEmoteRanges(string emotesTag)
    {
        var ranges = new List<(int, int, string)>();
        if (string.IsNullOrWhiteSpace(emotesTag))
            return ranges;

        foreach (var emote in emotesTag.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = emote.IndexOf(':');
            if (colon <= 0)
                continue;

            var id = emote[..colon];
            foreach (var range in emote[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = range.IndexOf('-');
                if (dash > 0
                    && int.TryParse(range[..dash], out var start)
                    && int.TryParse(range[(dash + 1)..], out var end))
                {
                    ranges.Add((start, end, id));
                }
            }
        }

        ranges.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return ranges;
    }

    private static string Join(Rune[] runes, int start, int end)
    {
        var builder = new StringBuilder();
        for (var i = start; i < end; i++)
            builder.Append(runes[i].ToString());
        return builder.ToString();
    }
}
