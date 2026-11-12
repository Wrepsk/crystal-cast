using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserLifecycleTests
{
    [Fact]
    public void LifecycleMovesFromCreatedToRunningToStopped()
    {
        var lifecycle = new BrowserLifecycle();

        Assert.Equal(BrowserLifecycleState.Created, lifecycle.State);
        Assert.True(lifecycle.TryStart());
        Assert.True(lifecycle.CanAcceptCommands);
        Assert.True(lifecycle.TryMarkRunning());
        Assert.Equal(BrowserLifecycleState.Running, lifecycle.State);
        Assert.True(lifecycle.TryBeginStopping());
        Assert.False(lifecycle.CanAcceptCommands);
        lifecycle.MarkStopped();

        Assert.Equal(BrowserLifecycleState.Stopped, lifecycle.State);
        Assert.False(lifecycle.TryStart());
        Assert.False(lifecycle.TryBeginStopping());
    }

    [Fact]
    public void StopDuringInitializationPreventsLateRunningTransition()
    {
        var lifecycle = new BrowserLifecycle();
        lifecycle.TryStart();

        Assert.True(lifecycle.TryBeginStopping());
        Assert.False(lifecycle.TryMarkRunning());
        Assert.False(lifecycle.CanAcceptCommands);

        lifecycle.MarkStopped();
        Assert.Equal(BrowserLifecycleState.Stopped, lifecycle.State);
    }

    [Fact]
    public void FaultCannotOverwriteStoppingState()
    {
        var lifecycle = new BrowserLifecycle();
        lifecycle.TryStart();
        lifecycle.TryBeginStopping();

        lifecycle.MarkFaulted();

        Assert.Equal(BrowserLifecycleState.Stopping, lifecycle.State);
    }

    [Fact]
    public void ConcurrentStopRequestsHaveSingleWinner()
    {
        var lifecycle = new BrowserLifecycle();
        lifecycle.TryStart();
        var results = new bool[32];

        Parallel.For(0, results.Length, index => results[index] = lifecycle.TryBeginStopping());

        Assert.Single(results, result => result);
        Assert.Equal(BrowserLifecycleState.Stopping, lifecycle.State);
    }
}
