using CrystalCast.Twitch;

namespace CrystalCast.Tests;

public sealed class TwitchThirdPartyEmoteTests
{
    [Fact]
    public void Reads7TvChannelEmotesAsStillPngs()
    {
        const string json = """
            {"id":"u","emote_set":{"emotes":[
              {"id":"a","name":"catJAM","data":{"animated":true,"flags":0,"host":{"url":"//cdn.7tv.app/emote/A","files":[
                {"name":"2x.webp","width":64,"height":64,"format":"WEBP"},{"name":"2x.gif","width":64,"height":64,"format":"GIF"}]}}},
              {"id":"b","name":"wideHappy","data":{"animated":false,"flags":0,"host":{"url":"//cdn.7tv.app/emote/B","files":[
                {"name":"2x.png","width":128,"height":64,"format":"PNG"}]}}},
              {"id":"c","name":"RainTime","data":{"animated":false,"flags":256,"host":{"url":"//cdn.7tv.app/emote/C","files":[
                {"name":"2x.png","width":64,"height":64,"format":"PNG"}]}}},
              {"id":"d","name":"webpOnly","data":{"animated":false,"host":{"url":"//cdn.7tv.app/emote/D","files":[{"name":"2x.webp","width":64,"height":64}]}}}
            ]}}
            """;
        var emotes = TwitchThirdPartyEmotes.ParseSevenTv(json);

        Assert.Equal(["catJAM", "wideHappy", "RainTime"], emotes.Select(emote => emote.Name));
        Assert.All(emotes, emote => Assert.EndsWith(".png", emote.Url));
        // Animated: 7TV's still frame, which loads where GIF may not.
        Assert.Equal("https://cdn.7tv.app/emote/A/2x_static.png", emotes[0].Url);
        Assert.Equal("https://cdn.7tv.app/emote/B/2x.png", emotes[1].Url);
        Assert.Equal(2.0f, emotes[1].Aspect);
        Assert.True(emotes[2].ZeroWidth);
        Assert.All(emotes, emote => Assert.Equal("7TV", emote.Provider));
    }

    [Fact]
    public void Reads7TvGlobalSets()
    {
        const string json = """{"id":"g","emotes":[{"name":"EZ","data":{"animated":false,"host":{"url":"//cdn.7tv.app/emote/E","files":[{"name":"2x.png","width":56,"height":56}]}}}]}""";
        Assert.Equal("EZ", Assert.Single(TwitchThirdPartyEmotes.ParseSevenTv(json)).Name);
    }

    [Fact]
    public void ReadsBttvChannelAndSharedEmotes()
    {
        const string json = """{"id":"x","channelEmotes":[{"id":"111","code":"monkaS","imageType":"png"}],"sharedEmotes":[{"id":"222","code":"catJAM","imageType":"gif"}]}""";
        var emotes = TwitchThirdPartyEmotes.ParseBttvChannel(json);

        Assert.Equal(["monkaS", "catJAM"], emotes.Select(emote => emote.Name));
        Assert.Equal("https://cdn.betterttv.net/emote/111/2x.png", emotes[0].Url);
        Assert.Equal("https://cdn.betterttv.net/emote/222/2x.png", emotes[1].Url);
        // BTTV doesn't list sizes; the shape is learned when the picture loads.
        Assert.Equal(0.0f, emotes[0].Aspect);
    }

    [Fact]
    public void ReadsBttvGlobals()
    {
        Assert.Equal(":tf:", Assert.Single(TwitchThirdPartyEmotes.ParseBttvGlobal("""[{"id":"54fa8f1401e468494b85b537","code":":tf:","imageType":"png"}]""")).Name);
    }

    [Fact]
    public void ReadsFfzSets()
    {
        const string json = """
            {"default_sets":[3],"sets":{"3":{"emoticons":[
              {"id":9,"name":"ZreknarF","width":40,"height":30,"urls":{"1":"https://cdn.frankerfacez.com/emote/9/1","2":"https://cdn.frankerfacez.com/emote/9/2"}},
              {"id":10,"name":"LilZ","width":28,"height":28,"urls":{"1":"//cdn.frankerfacez.com/emote/10/1"}}]}}}
            """;
        var emotes = TwitchThirdPartyEmotes.ParseFfz(json);

        Assert.Equal("https://cdn.frankerfacez.com/emote/9/2", emotes[0].Url);
        Assert.Equal(40.0f / 30.0f, emotes[0].Aspect, 3);
        Assert.Equal("https://cdn.frankerfacez.com/emote/10/1", emotes[1].Url);
    }

    [Fact]
    public void ChannelEmotesWinOverGlobalOnesAnd7TvOverTheOthers()
    {
        ThirdPartyEmote Emote(string name, string provider) => new(name, $"https://cdn.7tv.app/{provider}/{name}", 1.0f, false, provider);
        var merged = TwitchThirdPartyEmotes.Merge(
        [
            [Emote("Same", "7TV-channel")],
            [Emote("Same", "BTTV-channel"), Emote("OnlyBttv", "BTTV-channel")],
            [],
            [Emote("Same", "7TV-global"), Emote("OnlyGlobal", "7TV-global")],
        ]);

        Assert.Equal("7TV-channel", merged["Same"].Provider);
        Assert.Equal("BTTV-channel", merged["OnlyBttv"].Provider);
        Assert.Equal("7TV-global", merged["OnlyGlobal"].Provider);
        Assert.False(merged.ContainsKey("same"));
    }

    [Theory]
    [InlineData("https://cdn.7tv.app/emote/A/2x.gif", true)]
    [InlineData("https://cdn.betterttv.net/emote/1/2x.png", true)]
    [InlineData("https://cdn.frankerfacez.com/emote/9/2", true)]
    [InlineData("https://cdn.7tv.app.example.com/x.png", false)]
    [InlineData("http://cdn.7tv.app/emote/A/2x.gif", false)]
    public void LoadsPicturesFromTheEmoteHosts(string url, bool allowed)
    {
        Assert.Equal(allowed, TwitchImageCache.IsAllowed(url));
    }

    [Fact]
    public void ShowsThemByDefault()
    {
        Assert.True(new Configuration().TwitchThirdPartyEmotes);
    }
}
