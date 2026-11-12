using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class CaptureFrameDrainerTests
{
    [Fact]
    public void BurstClosesSupersededFramesAndReturnsNewest()
    {
        var frames = new Queue<int>([1, 2, 3]);
        var closed = new List<int>();

        var newest = CaptureFrameDrainer.DrainNewest(
            () => frames.TryDequeue(out var frame) ? frame : 0,
            frame => frame == 0,
            closed.Add,
            out var discarded);

        Assert.Equal(3, newest);
        Assert.Equal([1, 2], closed);
        Assert.Equal(2, discarded);
    }

    [Fact]
    public void EmptyPoolReturnsWithoutClosingAnything()
    {
        var closed = new List<int>();

        var newest = CaptureFrameDrainer.DrainNewest(
            () => 0,
            frame => frame == 0,
            closed.Add,
            out var discarded);

        Assert.Equal(0, newest);
        Assert.Empty(closed);
        Assert.Equal(0, discarded);
    }
}
