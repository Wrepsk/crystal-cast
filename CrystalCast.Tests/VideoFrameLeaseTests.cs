using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class VideoFrameLeaseTests
{
    [Fact]
    public void PooledFrameRemainsAliveUntilOwnerAndConsumerReleaseIt()
    {
        var owner = VideoFrame.Rent(13, 7, 1, 2);
        Assert.Equal(13 * 7 * 4, owner.PixelLength);
        Assert.True(owner.Pixels.Length >= owner.PixelLength);
        Assert.True(owner.TryAcquire(out var consumer));

        owner.Dispose();
        consumer.Dispose();

        Assert.False(owner.TryAcquire(out _));
    }
}
