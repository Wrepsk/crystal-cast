using CrystalCast.YouTube;

namespace CrystalCast.Tests;

public sealed class YouTubeFeedTests
{
    // Shaped like YouTube's home page data, cut down to the parts the reader uses.
    private const string HomePage = """
        {"contents":{"twoColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"richGridRenderer":{"contents":[
          {"richItemRenderer":{"content":{"lockupViewModel":{
            "contentId":"aaaaaaaaaaa","contentType":"LOCKUP_CONTENT_TYPE_VIDEO",
            "contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailBottomOverlayViewModel":{"badges":[
              {"thumbnailBadgeViewModel":{"text":"12:34"}},{"thumbnailBadgeViewModel":{"text":"New"}}]}}]}},
            "metadata":{"lockupMetadataViewModel":{"title":{"content":"A new video"},
              "metadata":{"contentMetadataViewModel":{"metadataRows":[
                {"metadataParts":[{"text":{"content":"Some Channel"}}]},
                {"metadataParts":[{"text":{"content":"1.2M"}},{"text":{"content":"3 days ago"}}]}]}}}}}}}},
          {"richItemRenderer":{"content":{"lockupViewModel":{
            "contentId":"PLxxxxxxxxxxxxxxxx","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST"}}}},
          {"richItemRenderer":{"content":{"videoRenderer":{
            "videoId":"bbbbbbbbbbb","title":{"runs":[{"text":"An older "},{"text":"style entry"}]},
            "ownerText":{"runs":[{"text":"Other Channel"}]},"shortViewCountText":{"simpleText":"45K"},
            "publishedTimeText":{"simpleText":"1 month ago"},"lengthText":{"simpleText":"1:02:03"}}}}},
          {"richItemRenderer":{"content":{"lockupViewModel":{
            "contentId":"ccccccccccc","contentType":"LOCKUP_CONTENT_TYPE_VIDEO",
            "contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailBottomOverlayViewModel":{"badges":[
              {"thumbnailBadgeViewModel":{"text":"LIVE"}}]}}]}},
            "metadata":{"lockupMetadataViewModel":{"title":{"content":"A live stream"},
              "metadata":{"contentMetadataViewModel":{"metadataRows":[
                {"metadataParts":[{"text":{"content":"Live Channel"}}]},
                {"metadataParts":[{"text":{"content":"900 watching"}}]}]}}}}}}}},
          {"richSectionRenderer":{"content":{"richShelfRenderer":{"contents":[
            {"richItemRenderer":{"content":{"shortsLockupViewModel":{"entityId":"shorts-shelf-item-ddddddddddd"}}}}]}}}},
          {"richItemRenderer":{"content":{"adSlotRenderer":{"videoRenderer":{"videoId":"eeeeeeeeeee"}}}}},
          {"richItemRenderer":{"content":{"videoRenderer":{"videoId":"bbbbbbbbbbb","title":{"simpleText":"Duplicate"}}}}},
          {"richItemRenderer":{"content":{"videoRenderer":{"videoId":"bad id"}}}}
        ]}}}}]}}}
        """;

    [Fact]
    public void ReadsBothKindsOfEntries()
    {
        var videos = YouTubeFeed.Parse(HomePage);

        Assert.Equal(["aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc"], videos.Select(video => video.VideoId));

        var lockup = videos[0];
        Assert.Equal("A new video", lockup.Title);
        Assert.Equal("Some Channel", lockup.Channel);
        Assert.Equal("1.2M views", lockup.Views);
        Assert.Equal("3 days ago", lockup.Age);
        Assert.Equal("12:34", lockup.Duration);
        Assert.False(lockup.IsLive);

        var older = videos[1];
        Assert.Equal("An older style entry", older.Title);
        Assert.Equal("Other Channel", older.Channel);
        Assert.Equal("45K views", older.Views);
        Assert.Equal("1:02:03", older.Duration);
    }

    [Fact]
    public void MarksLiveStreamsWithoutALength()
    {
        var live = YouTubeFeed.Parse(HomePage).Single(video => video.VideoId == "ccccccccccc");
        Assert.True(live.IsLive);
        Assert.Equal(string.Empty, live.Duration);
        Assert.Equal("900 watching", live.Views);
    }

    [Fact]
    public void LeavesOutShortsAdsPlaylistsAndBrokenEntries()
    {
        var ids = YouTubeFeed.Parse(HomePage).Select(video => video.VideoId).ToArray();
        Assert.DoesNotContain("eeeeeeeeeee", ids);
        Assert.DoesNotContain("ddddddddddd", ids);
        Assert.Equal(3, ids.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"contents":{"feedNudgeRenderer":{"title":{"simpleText":"Try searching to get started"}}}}""")]
    public void FindsNoVideosInPagesWithoutAny(string json)
    {
        Assert.Empty(YouTubeFeed.Parse(json));
    }

    [Fact]
    public void BuildsLinksAndThumbnailsFromTheId()
    {
        var video = new YouTubeVideo("aaaaaaaaaaa", "t", "c", "v", "a", "1:00", false);
        Assert.Equal("https://www.youtube.com/watch?v=aaaaaaaaaaa", video.Url);
        Assert.Equal("https://i.ytimg.com/vi/aaaaaaaaaaa/mqdefault.jpg", video.ThumbnailUrl);
    }

    [Theory]
    [InlineData("https://www.youtube.com/", true)]
    [InlineData("https://consent.youtube.com/m?continue=x", true)]
    [InlineData("https://accounts.google.com/ServiceLogin", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("http://www.youtube.com/", false)]
    public void TheHiddenBrowserStaysOnYouTube(string url, bool allowed)
    {
        Assert.Equal(allowed, YouTubeHomeReader.IsAllowedPage(url));
    }
}
