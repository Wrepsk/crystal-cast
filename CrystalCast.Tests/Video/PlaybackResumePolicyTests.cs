using CrystalCast.Video;

namespace CrystalCast.Tests.Video;

public sealed class PlaybackResumePolicyTests
{
    private const string Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    private static MediaPlaybackTelemetry Playing(long positionMs, long durationMs, string videoId = "dQw4w9WgXcQ", bool live = false, bool ended = false) =>
        new()
        {
            State = ended ? ScreenPlaybackState.Stopped : ScreenPlaybackState.Playing,
            PositionMs = positionMs,
            DurationMs = durationMs,
            VideoId = videoId,
            IsLive = live,
            Ended = ended,
        };

    [Fact]
    public void RemembersAVideoPartWayThrough()
    {
        var point = PlaybackResumePolicy.Capture(Url, Playing(125_000, 600_000));

        Assert.NotNull(point);
        Assert.Equal(125_000, point.PositionMs);
        Assert.Equal("dQw4w9WgXcQ", point.VideoId);
    }

    [Theory]
    [InlineData(2_000, 600_000)] // barely started
    [InlineData(597_000, 600_000)] // about to end
    [InlineData(30_000, 0)] // no duration: a live stream
    public void SkipsPositionsNotWorthResuming(long positionMs, long durationMs)
    {
        Assert.Null(PlaybackResumePolicy.Capture(Url, Playing(positionMs, durationMs)));
    }

    [Fact]
    public void NeverRemembersALiveStreamThatReportsADuration()
    {
        // YouTube live videos can report the time streamed so far as their duration.
        Assert.Null(PlaybackResumePolicy.Capture(Url, Playing(3_590_000, 3_600_000 * 2, live: true)));
    }

    [Fact]
    public void NeverRemembersAnEndedVideo()
    {
        Assert.Null(PlaybackResumePolicy.Capture(Url, Playing(300_000, 600_000, ended: true)));
    }

    [Fact]
    public void LoadingTelemetryIsNotSettled()
    {
        Assert.False(PlaybackResumePolicy.IsSettled(Playing(0, 0)));
        Assert.True(PlaybackResumePolicy.IsSettled(Playing(0, 600_000)));
        Assert.True(PlaybackResumePolicy.IsSettled(Playing(0, 0, live: true)));
    }

    [Fact]
    public void OnlyResumesTheSameLinkAndVideo()
    {
        var point = PlaybackResumePolicy.Capture(Url, Playing(125_000, 600_000))!;

        Assert.True(PlaybackResumePolicy.SameLink(point, Url));
        Assert.False(PlaybackResumePolicy.SameLink(point, "https://www.youtube.com/watch?v=9bZkp7q19f0"));
        Assert.True(PlaybackResumePolicy.SameVideo(point, Playing(0, 600_000)));
        Assert.False(PlaybackResumePolicy.SameVideo(point, Playing(0, 600_000, videoId: "9bZkp7q19f0")));
        Assert.True(PlaybackResumePolicy.SameVideo(point, Playing(0, 600_000, videoId: string.Empty)));
    }
}
