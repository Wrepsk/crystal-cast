using CrystalRenderer.D3D11;

namespace CrystalRenderer.Tests;

public sealed class DeferredFrameTrackerTests
{
    [Fact]
    public void TimeoutWorksForAFrameScheduledAtClockZero()
    {
        long now = 0;
        var frames = new DeferredFrameTracker(() => now);
        frames.Schedule();
        now = 1999;
        Assert.False(frames.IsStalled(TimeSpan.FromSeconds(2)));
        now = 2000;
        Assert.True(frames.IsStalled(TimeSpan.FromSeconds(2)));
        Assert.True(frames.Cancel());
        Assert.False(frames.HasWork);
        Assert.False(frames.IsStalled(TimeSpan.Zero));
    }

    [Fact]
    public void CallbackRemainsBusyUntilFlushCompletesAndCannotReenter()
    {
        var frames = new DeferredFrameTracker(() => 100);
        frames.Schedule();
        Assert.True(frames.TryBeginFlush());
        Assert.True(frames.HasWork);
        Assert.False(frames.TryBeginFlush());
        Assert.False(frames.Cancel());
        Assert.Throws<InvalidOperationException>(frames.Schedule);
        frames.EndFlush(true);
        Assert.False(frames.HasWork);
        Assert.Equal(1, frames.FlushedCount);
        frames.Schedule();
        Assert.Equal(2, frames.ScheduledCount);
    }

    [Fact]
    public void CancelledAndFailedFramesDoNotCountAsSuccessfulFlushes()
    {
        var frames = new DeferredFrameTracker(() => 100);
        frames.Schedule();
        frames.Cancel();
        Assert.False(frames.TryBeginFlush());
        frames.Schedule();
        frames.TryBeginFlush();
        frames.EndFlush(false);
        Assert.Equal(0, frames.FlushedCount);
        Assert.False(frames.HasWork);
    }
}
