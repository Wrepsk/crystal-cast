using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class CaptureFrameProcessingPolicyTests
{
    [Theory]
    [InlineData(false, 1280, 720, 1280, 720, 0)]
    [InlineData(true, 1280, 720, 0, 720, 1)]
    [InlineData(true, 1280, 720, 1920, 1080, 2)]
    [InlineData(true, 1280, 720, 1280, 720, 3)]
    public void FrameActionSeparatesPauseEmptyResizeAndPublish(
        bool enabled,
        int currentWidth,
        int currentHeight,
        int frameWidth,
        int frameHeight,
        int expected)
    {
        Assert.Equal((CaptureFrameAction)expected, CaptureFrameProcessingPolicy.Decide(
            enabled,
            currentWidth,
            currentHeight,
            frameWidth,
            frameHeight));
    }

    [Fact]
    public void RateLimiterSuppressesEarlyFrameWithoutAnyTimerOrPolling()
    {
        var limiter = new CaptureFrameRateLimiter();
        const long frequency = 10_000;

        Assert.True(limiter.ShouldPublish(10, 1_000, frequency));
        Assert.False(limiter.ShouldPublish(10, 1_100, frequency));
        Assert.True(limiter.ShouldPublish(10, 1_900, frequency));
    }

    [Fact]
    public void HighFrameRatesPublishEveryArrival()
    {
        var limiter = new CaptureFrameRateLimiter();

        Assert.True(limiter.ShouldPublish(60, 1, 10_000));
        Assert.True(limiter.ShouldPublish(60, 2, 10_000));
    }

    [Fact]
    public void PausedFrameClosesWithoutPublishing()
    {
        var operations = new List<string>();

        CaptureFrameCompletion.Complete(
            CaptureFrameAction.DrainPaused,
            () => operations.Add("publish"),
            () => operations.Add("close"),
            () => operations.Add("recreate"));

        Assert.Equal(["close"], operations);
    }

    [Fact]
    public void ResizeClosesCheckedOutFrameBeforeRecreatingPool()
    {
        var operations = new List<string>();

        CaptureFrameCompletion.Complete(
            CaptureFrameAction.Recreate,
            () => operations.Add("publish"),
            () => operations.Add("close"),
            () => operations.Add("recreate"));

        Assert.Equal(["close", "recreate"], operations);
    }
}
