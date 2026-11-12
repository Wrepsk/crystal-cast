using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class CaptureResourceRetirementQueueTests
{
    [Fact]
    public void AcknowledgementDisposesEveryRetiredResource()
    {
        using var queue = new CaptureResourceRetirementQueue<RecordingResource>(4);
        var first = new RecordingResource();
        var second = new RecordingResource();

        queue.Retire(first);
        queue.Retire(second);
        queue.AcknowledgeCurrent();

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Equal(0, queue.Count);
        Assert.True(queue.CanRetire);
    }

    [Fact]
    public void FifthUnacknowledgedResourceIsRejected()
    {
        using var queue = new CaptureResourceRetirementQueue<RecordingResource>(4);
        for (var index = 0; index < 4; index++)
            queue.Retire(new RecordingResource());

        var rejected = new RecordingResource();
        Assert.False(queue.CanRetire);
        Assert.Throws<InvalidOperationException>(() => queue.Retire(rejected));
        Assert.False(rejected.Disposed);
    }

    [Fact]
    public void DisposeReleasesResourcesStillAwaitingAcknowledgement()
    {
        var queue = new CaptureResourceRetirementQueue<RecordingResource>(4);
        var resource = new RecordingResource();
        queue.Retire(resource);

        queue.Dispose();

        Assert.True(resource.Disposed);
        Assert.Equal(0, queue.Count);
    }

    private sealed class RecordingResource : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
