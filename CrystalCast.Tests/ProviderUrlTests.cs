using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class ProviderUrlTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ", YouTubeSourceKind.Video)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", YouTubeSourceKind.Video)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", YouTubeSourceKind.Video)]
    [InlineData("https://www.youtube.com/playlist?list=PL123456789012", YouTubeSourceKind.Playlist)]
    public void YouTubeParsesSupportedSources(string input, YouTubeSourceKind expectedKind)
    {
        Assert.True(YouTubeVideoId.TryParseSource(input, out var source));
        Assert.Equal(expectedKind, source.Kind);
    }

    [Theory]
    [InlineData("https://evilyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com@evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    public void YouTubeRejectsSpoofedHostsAndUnsupportedSchemes(string input)
    {
        Assert.False(YouTubeVideoId.TryParseSource(input, out _));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/OpenAI_Test", TwitchSourceKind.Channel)]
    [InlineData("https://www.twitch.tv/videos/123456789", TwitchSourceKind.Video)]
    [InlineData("v123456789", TwitchSourceKind.Video)]
    public void TwitchParsesChannelsAndVideos(string input, TwitchSourceKind expectedKind)
    {
        Assert.True(TwitchVideoId.TryParseSource(input, out var source));
        Assert.Equal(expectedKind, source.Kind);
    }

    [Theory]
    [InlineData("ftp://www.twitch.tv/videos/123456789")]
    [InlineData("https://clips.twitch.tv/ExampleClip")]
    [InlineData("https://example.com/videos/123456789")]
    [InlineData("https://www.twitch.tv.evil.example/videos/123456789")]
    [InlineData("https://www.twitch.tv@evil.example/videos/123456789")]
    public void TwitchRejectsUnsupportedSources(string input)
    {
        Assert.False(TwitchVideoId.TryParseSource(input, out _));
    }

    [Theory]
    [InlineData("https://www.dailymotion.com/video/x8abcde", DailymotionSourceKind.Video)]
    [InlineData("https://dai.ly/x8abcde", DailymotionSourceKind.Video)]
    [InlineData("https://www.dailymotion.com/playlist/x7abcde", DailymotionSourceKind.Playlist)]
    public void DailymotionParsesVideosAndPlaylists(string input, DailymotionSourceKind expectedKind)
    {
        Assert.True(DailymotionVideoId.TryParseSource(input, out var source));
        Assert.Equal(expectedKind, source.Kind);
    }

    [Theory]
    [InlineData("ftp://www.dailymotion.com/video/x8abcde")]
    [InlineData("https://example.com/video/x8abcde")]
    [InlineData("https://www.dailymotion.com.evil.example/video/x8abcde")]
    [InlineData("https://www.dailymotion.com@evil.example/video/x8abcde")]
    public void DailymotionRejectsUnsupportedSources(string input)
    {
        Assert.False(DailymotionVideoId.TryParseSource(input, out _));
    }

    [Theory]
    [InlineData("https://vimeo.com/123456789")]
    [InlineData("https://player.vimeo.com/video/123456789?h=abcdef1234")]
    public void VimeoParsesSupportedUrls(string input)
    {
        Assert.True(VimeoVideoId.TryParseSource(input, out _));
    }

    [Theory]
    [InlineData("ftp://vimeo.com/123456789")]
    [InlineData("https://example.com/123456789")]
    [InlineData("https://vimeo.com.evil.example/123456789")]
    [InlineData("https://vimeo.com@evil.example/123456789")]
    public void VimeoRejectsUnsupportedSources(string input)
    {
        Assert.False(VimeoVideoId.TryParseSource(input, out _));
    }

    [Theory]
    [InlineData("https://example.com/media")]
    [InlineData("http://localhost:8080/player")]
    public void GenericWebAcceptsHttpAndHttps(string input)
    {
        Assert.True(GenericWebUrl.TryParseSource(input, out _));
    }

    [Theory]
    [InlineData("file:///C:/video.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.com/media")]
    [InlineData("not a URL")]
    public void GenericWebRejectsOtherSchemesAndInvalidUrls(string input)
    {
        Assert.False(GenericWebUrl.TryParseSource(input, out _));
    }
}
