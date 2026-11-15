using System.Numerics;
using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class SurfaceClickTrackerTests
{
    private static readonly Vector2 Spot = new(400, 300);

    [Fact]
    public void AQuickStillPressOverTheGameIsAClick()
    {
        var tracker = new SurfaceClickTracker();

        Assert.False(tracker.Update(down: true, overGame: true, Spot, nowMs: 1_000));
        Assert.False(tracker.Update(down: true, overGame: true, Spot, nowMs: 1_050));
        Assert.True(tracker.Update(down: false, overGame: true, Spot + new Vector2(2, 1), nowMs: 1_120));
    }

    [Fact]
    public void ADragIsNotAClick()
    {
        var tracker = new SurfaceClickTracker();

        tracker.Update(down: true, overGame: true, Spot, nowMs: 1_000);
        Assert.False(tracker.Update(down: false, overGame: true, Spot + new Vector2(60, 0), nowMs: 1_100));
    }

    [Fact]
    public void AHeldPressIsNotAClick()
    {
        // The game locks the cursor while the camera turns, so a long press may not move it at all.
        var tracker = new SurfaceClickTracker();

        tracker.Update(down: true, overGame: true, Spot, nowMs: 1_000);
        Assert.False(tracker.Update(down: false, overGame: true, Spot, nowMs: 2_000));
    }

    [Fact]
    public void APressOnAWindowIsNotAClick()
    {
        var tracker = new SurfaceClickTracker();

        tracker.Update(down: true, overGame: false, Spot, nowMs: 1_000);
        Assert.False(tracker.Update(down: false, overGame: true, Spot, nowMs: 1_100));
    }

    [Fact]
    public void ResetForgetsAPressInProgress()
    {
        var tracker = new SurfaceClickTracker();

        tracker.Update(down: true, overGame: true, Spot, nowMs: 1_000);
        tracker.Reset();
        Assert.False(tracker.Update(down: false, overGame: true, Spot, nowMs: 1_100));
    }
}
