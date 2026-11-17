using System.Net;
using CrystalCast.Twitch;

namespace CrystalCast.Tests;

public sealed class TwitchOwnMessageTests
{
    [Fact]
    public void ShowsOwnMessagesWithTheNameAndBadgesTwitchReports()
    {
        var log = new TwitchChatLog();
        log.Reset("c");
        log.Apply(TwitchIrcMessage.Parse("@badges=subscriber/12;color=#FF0000;display-name=Viewer :tmi.twitch.tv USERSTATE #c")!);

        log.AddOwnMessage("m1", "viewer", "fallback", [new TwitchChatSegment(TwitchChatSegmentKind.Text, "hello")]);

        var line = Assert.Single(log.Lines);
        Assert.Equal("Viewer", line.DisplayName);
        Assert.Equal("#FF0000", line.Color);
        Assert.Equal([new TwitchBadge("subscriber", "12")], line.Badges);
        Assert.Equal("m1", line.Id);
    }

    [Fact]
    public void NeverShowsAMessageTwice()
    {
        var log = new TwitchChatLog();
        log.Reset("c");
        log.AddOwnMessage("m1", "viewer", "Viewer", [new TwitchChatSegment(TwitchChatSegmentKind.Text, "hello")]);
        // Twitch delivering the same message afterwards.
        log.Apply(TwitchIrcMessage.Parse("@id=m1 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #c :hello")!);
        Assert.Single(log.Lines);

        // And the other way round.
        log.Apply(TwitchIrcMessage.Parse("@id=m2 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #c :again")!);
        log.AddOwnMessage("m2", "viewer", "Viewer", [new TwitchChatSegment(TwitchChatSegmentKind.Text, "again")]);
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void UsesTheLoginWithoutAUserState()
    {
        var log = new TwitchChatLog();
        log.Reset("c");
        log.AddOwnMessage("m1", "Viewer", "Viewer", []);
        Assert.Equal("Viewer", log.Lines.Single().DisplayName);
        Assert.Equal("viewer", log.Lines.Single().Login);
        Assert.Null(log.Own);
    }

    [Fact]
    public void ForgetsTheUserStateForAnotherChannel()
    {
        var log = new TwitchChatLog();
        log.Reset("c");
        log.Apply(TwitchIrcMessage.Parse("@display-name=Viewer :tmi.twitch.tv USERSTATE #c")!);
        log.Reset("other");
        Assert.Null(log.Own);
    }

    [Fact]
    public void FindsEmotesInTypedMessagesByName()
    {
        var ids = new Dictionary<string, string> { ["Kappa"] = "25", ["chanHype"] = "e1" };
        var segments = TwitchChatLog.SegmentByNames("hi Kappa  there chanHype", word => ids.GetValueOrDefault(word));

        Assert.Equal(
            [
                new TwitchChatSegment(TwitchChatSegmentKind.Text, "hi "),
                new TwitchChatSegment(TwitchChatSegmentKind.Emote, "Kappa", "25"),
                new TwitchChatSegment(TwitchChatSegmentKind.Text, "  there "),
                new TwitchChatSegment(TwitchChatSegmentKind.Emote, "chanHype", "e1"),
            ],
            segments);
    }

    [Fact]
    public void ReadsTheIdTwitchGaveTheMessage()
    {
        var result = TwitchAccount.ReadSendResult(HttpStatusCode.OK, """{"data":[{"message_id":"abc-123","is_sent":true}]}""");
        Assert.True(result.Sent);
        Assert.Equal("abc-123", result.MessageId);
    }
}
