using System.Text.Json;
using CrystalCast.Twitch;

namespace CrystalCast.Tests;

public sealed class TwitchBadgeTests
{
    [Fact]
    public void KnowsCommonBadgesWithoutALogin()
    {
        var catalog = new TwitchBadgeCatalog();

        var moderator = catalog.Resolve(new TwitchBadge("moderator", "1"));
        Assert.Equal("Moderator", moderator?.Title);
        Assert.StartsWith("https://static-cdn.jtvnw.net/badges/v1/", moderator?.Url);
        Assert.Null(catalog.Resolve(new TwitchBadge("bits", "1000")));
    }

    [Fact]
    public void PrefersTheChannelsOwnBadgesThenTwitchsList()
    {
        var catalog = new TwitchBadgeCatalog();
        catalog.SetGlobal([(new TwitchBadge("subscriber", "0"), new TwitchBadgeImage("https://static-cdn.jtvnw.net/badges/v1/global/2", "Subscriber"))]);
        catalog.SetChannel("42", [(new TwitchBadge("subscriber", "12"), new TwitchBadgeImage("https://static-cdn.jtvnw.net/badges/v1/own/2", "1-Year Subscriber"))]);

        Assert.Equal("1-Year Subscriber", catalog.Resolve(new TwitchBadge("subscriber", "12"))?.Title);
        Assert.Equal("https://static-cdn.jtvnw.net/badges/v1/global/2", catalog.Resolve(new TwitchBadge("subscriber", "0"))?.Url);
        // Not in either list: the built-in picture for the set.
        Assert.Equal("Subscriber", catalog.Resolve(new TwitchBadge("subscriber", "3"))?.Title);

        catalog.ClearChannel();
        Assert.Equal(string.Empty, catalog.ChannelId);
        Assert.NotEqual("1-Year Subscriber", catalog.Resolve(new TwitchBadge("subscriber", "12"))?.Title);
    }

    [Fact]
    public void ReadsTwitchsBadgeLists()
    {
        const string json = """
            {"data":[
              {"set_id":"bits","versions":[
                {"id":"1","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/a/1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/a/2","title":"cheer 1"},
                {"id":"100","image_url_1x":"https://static-cdn.jtvnw.net/badges/v1/b/1","title":"cheer 100"},
                {"id":"","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/c/2"}]},
              {"set_id":"","versions":[{"id":"1","image_url_2x":"https://static-cdn.jtvnw.net/badges/v1/d/2"}]}]}
            """;
        var page = JsonSerializer.Deserialize<TwitchAccount.HelixPage<TwitchAccount.HelixBadgeSet>>(json);
        var badges = TwitchAccount.ToBadges(page!.Data);

        Assert.Equal(2, badges.Count);
        Assert.Equal((new TwitchBadge("bits", "1"), new TwitchBadgeImage("https://static-cdn.jtvnw.net/badges/v1/a/2", "cheer 1")), badges[0]);
        // No 2x picture: the 1x one.
        Assert.Equal("https://static-cdn.jtvnw.net/badges/v1/b/1", badges[1].Image.Url);
    }

    [Fact]
    public void LearnsTheChannelIdFromRoomState()
    {
        var log = new TwitchChatLog();
        log.Reset("c");
        log.Apply(TwitchIrcMessage.Parse("@emote-only=0;room-id=12826;slow=0 :tmi.twitch.tv ROOMSTATE #c")!);
        Assert.Equal("12826", log.RoomId);

        // A mode change without the ID keeps it.
        log.Apply(TwitchIrcMessage.Parse("@slow=30 :tmi.twitch.tv ROOMSTATE #c")!);
        Assert.Equal("12826", log.RoomId);

        log.Reset("other");
        Assert.Equal(string.Empty, log.RoomId);
    }

    [Theory]
    [InlineData("https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0", true)]
    [InlineData("http://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0", false)]
    [InlineData("https://static-cdn.jtvnw.net.example.com/x.png", false)]
    [InlineData("https://example.com/x.png", false)]
    public void LoadsPicturesOnlyFromTwitchsImageHost(string url, bool allowed)
    {
        Assert.Equal(allowed, TwitchImageCache.IsAllowed(url));
    }

    [Fact]
    public void BuildsEmoteLinks()
    {
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_abc/static/dark/2.0", TwitchImageCache.EmoteUrl("emotesv2_abc"));
    }
}
