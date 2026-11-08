using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserSourceDetectorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", BrowserSourceProviderKind.YouTube)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", BrowserSourceProviderKind.YouTube)]
    [InlineData("https://www.twitch.tv/somechannel", BrowserSourceProviderKind.Twitch)]
    [InlineData("https://www.dailymotion.com/video/x8abcd1", BrowserSourceProviderKind.Dailymotion)]
    [InlineData("https://vimeo.com/76979871", BrowserSourceProviderKind.Vimeo)]
    [InlineData("https://example.com/page", BrowserSourceProviderKind.GenericWeb)]
    public void FullLinksAreMatchedBySite(string input, BrowserSourceProviderKind expected)
    {
        Assert.True(BrowserSourceDetector.TryDetect(input, BrowserSourceProviderKind.YouTube, out var detected));
        Assert.Equal(expected, detected.ProviderKind);
        Assert.Equal(expected == BrowserSourceProviderKind.GenericWeb, detected.IsGenericWebFallback);
    }

    [Fact]
    public void LinksAreDetectedRegardlessOfTheCurrentProvider()
    {
        Assert.True(BrowserSourceDetector.TryDetect("https://www.youtube.com/watch?v=dQw4w9WgXcQ", BrowserSourceProviderKind.Twitch, out var detected));
        Assert.Equal(BrowserSourceProviderKind.YouTube, detected.ProviderKind);
    }

    [Fact]
    public void SchemelessLinksGetHttps()
    {
        Assert.True(BrowserSourceDetector.TryDetect("youtube.com/watch?v=dQw4w9WgXcQ", BrowserSourceProviderKind.Twitch, out var detected));
        Assert.Equal(BrowserSourceProviderKind.YouTube, detected.ProviderKind);
        Assert.Equal("https://youtube.com/watch?v=dQw4w9WgXcQ", detected.Url);
    }

    [Fact]
    public void BareIdsStayWithTheCurrentProvider()
    {
        // Valid as both a YouTube video ID and a Twitch channel name.
        const string ambiguous = "dQw4w9WgXcQ";

        Assert.True(BrowserSourceDetector.TryDetect(ambiguous, BrowserSourceProviderKind.YouTube, out var youTube));
        Assert.Equal(BrowserSourceProviderKind.YouTube, youTube.ProviderKind);

        Assert.True(BrowserSourceDetector.TryDetect(ambiguous, BrowserSourceProviderKind.Twitch, out var twitch));
        Assert.Equal(BrowserSourceProviderKind.Twitch, twitch.ProviderKind);
    }

    [Fact]
    public void BareIdsAreRejectedForGenericWebScreens()
    {
        Assert.False(BrowserSourceDetector.TryDetect("dQw4w9WgXcQ", BrowserSourceProviderKind.GenericWeb, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a link")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    public void NonLinksAreRejected(string input)
    {
        Assert.False(BrowserSourceDetector.TryDetect(input, BrowserSourceProviderKind.GenericWeb, out _));
    }
}
