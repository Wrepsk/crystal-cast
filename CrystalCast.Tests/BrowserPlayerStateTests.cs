using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserPlayerStateTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not a video")]
    public void ALinkThatDoesNotParseDoesNotCrash(string input)
    {
        Assert.False(BrowserSourceDescriptors.YouTube.TryParse(input, out var source));

        var state = new BrowserPlayerState(BrowserSourceDescriptors.YouTube, source, 1.0f);

        Assert.Equal(ScreenPlaybackState.Stopped, state.Telemetry.State);
        Assert.Equal(string.Empty, state.Telemetry.VideoId);
        Assert.Equal(string.Empty, state.Telemetry.CanonicalUrl);
    }
}
