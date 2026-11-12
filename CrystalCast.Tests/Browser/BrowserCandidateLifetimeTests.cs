using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserCandidateLifetimeTests
{
    [Fact]
    public void FailedCandidateIsDisposedExactlyOnce()
    {
        var candidate = new FakeFrameSource();

        var accepted = BrowserCandidateLifetime.TryUse(
            candidate,
            () => throw new InvalidOperationException("start failed"),
            out var error);

        Assert.False(accepted);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, candidate.DisposeCount);
    }

    [Fact]
    public void AcceptedCandidateRemainsOwnedByCaller()
    {
        var candidate = new FakeFrameSource();
        var actionCount = 0;

        var accepted = BrowserCandidateLifetime.TryUse(candidate, () => actionCount++, out var error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal(1, actionCount);
        Assert.Equal(0, candidate.DisposeCount);
    }

    private sealed class FakeFrameSource : IVideoFrameSource
    {
        public int DisposeCount { get; private set; }
        public string Name => "fake";
        public int Width => 1;
        public int Height => 1;
        public float FramesPerSecond => 1;
        public bool IsRunning => false;
        public string Status => string.Empty;
        public void Start() { }
        public void Stop() { }
        public bool TryGetLatestFrame(out VideoFrame frame)
        {
            frame = null!;
            return false;
        }

        public void Dispose() => DisposeCount++;
    }
}
