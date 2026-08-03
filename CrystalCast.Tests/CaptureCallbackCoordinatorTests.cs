using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class CaptureCallbackCoordinatorTests
{
    [Fact]
    public async Task OnlyOneCallbackRunsAtATime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = new CaptureCallbackCoordinator();
        var releaseFirst = new ManualResetEventSlim();
        var firstEntered = new ManualResetEventSlim();
        var concurrent = 0;
        var maximumConcurrent = 0;

        void Callback()
        {
            var current = Interlocked.Increment(ref concurrent);
            maximumConcurrent = Math.Max(maximumConcurrent, current);
            firstEntered.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(5), cancellationToken);
            Interlocked.Decrement(ref concurrent);
        }

        var first = Task.Run(() => coordinator.RunCallback(Callback, _ => { }), cancellationToken);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        var second = Task.Run(() => coordinator.RunCallback(Callback, _ => { }), cancellationToken);
        await Task.Delay(50, cancellationToken);

        Assert.Equal(1, maximumConcurrent);
        releaseFirst.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximumConcurrent);
    }

    [Fact]
    public async Task StopWaitsForInFlightCallbackAndLaterEventsDoNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var coordinator = new CaptureCallbackCoordinator();
        var callbackEntered = new ManualResetEventSlim();
        var releaseCallback = new ManualResetEventSlim();
        var releaseRan = false;
        var callbackCount = 0;

        var callback = Task.Run(() => coordinator.RunCallback(() =>
        {
            Interlocked.Increment(ref callbackCount);
            callbackEntered.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(5), cancellationToken);
        }, _ => { }), cancellationToken);
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        Assert.True(coordinator.TryBeginStop());
        Assert.False(coordinator.TryBeginStop());

        var stop = Task.Run(
            () => coordinator.WaitForIdleAndRun(() => releaseRan = true),
            cancellationToken);
        await Task.Delay(50, cancellationToken);
        Assert.False(stop.IsCompleted);

        releaseCallback.Set();
        await Task.WhenAll(callback, stop);
        coordinator.RunCallback(() => Interlocked.Increment(ref callbackCount), _ => { });

        Assert.True(releaseRan);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void FatalCallbackErrorsAreReportedOnce()
    {
        var coordinator = new CaptureCallbackCoordinator();
        var reports = 0;
        var callbacks = 0;

        coordinator.RunCallback(() =>
        {
            callbacks++;
            throw new InvalidOperationException("first");
        }, _ => reports++);
        coordinator.RunCallback(() =>
        {
            callbacks++;
            throw new InvalidOperationException("second");
        }, _ => reports++);

        Assert.Equal(1, reports);
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void FatalReporterExceptionsDoNotEscapeTheNativeCallbackBoundary()
    {
        var coordinator = new CaptureCallbackCoordinator();

        var exception = Record.Exception(() => coordinator.RunCallback(
            () => throw new InvalidOperationException("callback"),
            _ => throw new InvalidOperationException("reporter")));

        Assert.Null(exception);
    }
}
