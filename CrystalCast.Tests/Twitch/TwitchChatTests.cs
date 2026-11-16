using System.Numerics;
using CrystalCast.Twitch;
using CrystalCast.Windows.Panels;

namespace CrystalCast.Tests;

public sealed class TwitchChatTests
{
    private const string Privmsg =
        "@badge-info=subscriber/8;badges=subscriber/6,premium/1;color=#1E90FF;display-name=Viewer;emotes=25:0-4;id=abc-1;mod=0 "
        + ":viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #SomeChannel :Kappa hello there";

    [Fact]
    public void ParsesTagsPrefixAndText()
    {
        var message = TwitchIrcMessage.Parse(Privmsg)!;

        Assert.Equal("PRIVMSG", message.Command);
        Assert.Equal("somechannel", message.Channel);
        Assert.Equal("viewer", message.PrefixLogin);
        Assert.Equal("Kappa hello there", message.Trailing);
        Assert.Equal("#1E90FF", message.Tag("color"));
        Assert.Equal(string.Empty, message.Tag("missing"));
    }

    [Fact]
    public void ParsesPingWithoutTags()
    {
        var message = TwitchIrcMessage.Parse("PING :tmi.twitch.tv\r\n")!;
        Assert.Equal("PING", message.Command);
        Assert.Equal("tmi.twitch.tv", message.Trailing);
    }

    [Theory]
    [InlineData(@"a\sb", "a b")]
    [InlineData(@"a\:b", "a;b")]
    [InlineData(@"a\\b", @"a\b")]
    [InlineData(@"end\", "end")]
    public void UnescapesTagValues(string raw, string expected)
    {
        Assert.Equal(expected, TwitchIrcMessage.UnescapeTagValue(raw));
    }

    [Fact]
    public void SplitsEmotesFromText()
    {
        var segments = TwitchChatLog.Segment("Kappa hi Kappa", "25:0-4,9-13");

        Assert.Equal(
            [
                new TwitchChatSegment(TwitchChatSegmentKind.Emote, "Kappa", "25"),
                new TwitchChatSegment(TwitchChatSegmentKind.Text, " hi "),
                new TwitchChatSegment(TwitchChatSegmentKind.Emote, "Kappa", "25"),
            ],
            segments);
    }

    [Fact]
    public void CountsEmotePositionsInCodePoints()
    {
        // The emoji is two UTF-16 chars but one position for Twitch.
        var segments = TwitchChatLog.Segment("😀 Kappa", "25:2-6");

        Assert.Equal(new TwitchChatSegment(TwitchChatSegmentKind.Emote, "Kappa", "25"), segments[^1]);
    }

    [Fact]
    public void IgnoresEmoteRangesOutsideTheText()
    {
        var segments = TwitchChatLog.Segment("hi", "25:0-40");
        Assert.Equal([new TwitchChatSegment(TwitchChatSegmentKind.Text, "hi")], segments);
    }

    [Fact]
    public void ReadsMessagesAndActions()
    {
        var line = TwitchChatLog.FromPrivmsg(TwitchIrcMessage.Parse(Privmsg)!);
        Assert.Equal("Viewer", line.DisplayName);
        Assert.Equal([new TwitchBadge("subscriber", "6"), new TwitchBadge("premium", "1")], line.Badges);
        Assert.False(line.IsAction);

        var action = TwitchChatLog.FromPrivmsg(TwitchIrcMessage.Parse(":viewer!viewer@x PRIVMSG #c :\u0001ACTION waves\u0001")!);
        Assert.True(action.IsAction);
        Assert.Equal("waves", action.Segments.Single().Text);
    }

    [Theory]
    [InlineData("Viewer", "viewer", "Viewer")]
    [InlineData("", "viewer", "viewer")]
    [InlineData("ビューア", "viewer", "ビューア (viewer)")]
    public void ShowsTheLoginForNamesInOtherScripts(string display, string login, string expected)
    {
        Assert.Equal(expected, TwitchChatLog.DisplayName(display, login));
    }

    [Fact]
    public void IgnoresOtherChannelsAndTrimsOldLines()
    {
        var log = new TwitchChatLog();
        log.Reset("SomeChannel");
        log.Apply(TwitchIrcMessage.Parse(":a!a@x PRIVMSG #other :hi")!);
        Assert.Empty(log.Lines);

        for (var i = 0; i < TwitchChatLog.MaxLines + 10; i++)
            log.Apply(TwitchIrcMessage.Parse($":a!a@x PRIVMSG #somechannel :line {i}")!);

        Assert.Equal(TwitchChatLog.MaxLines, log.Lines.Count);
        Assert.Equal("line 10", log.Lines[0].Segments.Single().Text);
    }

    [Fact]
    public void AppliesDeletionsAndTimeouts()
    {
        var log = new TwitchChatLog();
        log.Reset("c");
        log.Apply(TwitchIrcMessage.Parse("@id=1 :a!a@x PRIVMSG #c :one")!);
        log.Apply(TwitchIrcMessage.Parse("@id=2 :b!b@x PRIVMSG #c :two")!);
        log.Apply(TwitchIrcMessage.Parse("@id=3 :b!b@x PRIVMSG #c :three")!);

        log.Apply(TwitchIrcMessage.Parse("@target-msg-id=1 :tmi.twitch.tv CLEARMSG #c :one")!);
        Assert.True(log.Lines[0].Deleted);
        Assert.False(log.Lines[1].Deleted);

        log.Apply(TwitchIrcMessage.Parse("@ban-duration=60 :tmi.twitch.tv CLEARCHAT #c :b")!);
        Assert.True(log.Lines[1].Deleted);
        Assert.True(log.Lines[2].Deleted);

        log.Apply(TwitchIrcMessage.Parse(":tmi.twitch.tv CLEARCHAT #c")!);
        Assert.Equal(TwitchChatLineKind.Notice, log.Lines[^1].Kind);
    }

    [Fact]
    public void TracksJoinAndChatModes()
    {
        var log = new TwitchChatLog();
        log.Reset("c");
        Assert.False(log.Joined);

        log.Apply(TwitchIrcMessage.Parse("@emote-only=0;followers-only=-1;r9k=0;slow=0;subs-only=0 :tmi.twitch.tv ROOMSTATE #c")!);
        Assert.True(log.Joined);
        Assert.Equal(string.Empty, TwitchPanel.DescribeModes(log.RoomState));

        // Later ROOMSTATEs carry only the mode that changed.
        log.Apply(TwitchIrcMessage.Parse("@followers-only=10 :tmi.twitch.tv ROOMSTATE #c")!);
        log.Apply(TwitchIrcMessage.Parse("@slow=30 :tmi.twitch.tv ROOMSTATE #c")!);
        Assert.Equal("Followers-only (10 min) · Slow mode (30s)", TwitchPanel.DescribeModes(log.RoomState));
    }

    [Fact]
    public void ReadsSubscriptionEvents()
    {
        var message = TwitchIrcMessage.Parse(
            @"@display-name=Fan;login=fan;msg-id=resub;system-msg=Fan\ssubscribed\sfor\s3\smonths! :tmi.twitch.tv USERNOTICE #c :great stream")!;
        var line = TwitchChatLog.FromUserNotice(message);

        Assert.Equal(TwitchChatLineKind.Event, line.Kind);
        Assert.Equal("Fan subscribed for 3 months!", line.EventText);
        Assert.Equal("great stream", line.Segments.Single().Text);
    }

    [Fact]
    public void KeepsNameColorsReadable()
    {
        var navy = TwitchChatColors.NameColor("x", "#000080");
        Assert.True(TwitchChatColors.Luminance(navy) >= TwitchChatColors.MinimumLuminance - 0.001f);

        var picked = TwitchChatColors.NameColor("x", "#FFFF00");
        Assert.Equal(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), picked);

        // No color picked: the same login always gets the same one.
        Assert.Equal(TwitchChatColors.NameColor("someone", string.Empty), TwitchChatColors.NameColor("someone", string.Empty));
    }
}
