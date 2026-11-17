using CrystalCast.Rendering;
using CrystalCast.Windows.Panels;

namespace CrystalCast.Tests;

public sealed class PickResizeTests
{
    [Theory]
    [InlineData(3.0f, 1.0f, false, 3.3f)]
    [InlineData(3.0f, -1.0f, false, 3.0f / 1.1f)]
    [InlineData(3.0f, 2.0f, false, 3.0f * 1.1f * 1.1f)]
    [InlineData(3.0f, 1.0f, true, 3.06f)]
    public void ScrollingChangesTheWidthInSteps(float width, float wheel, bool fine, float expected)
    {
        Assert.Equal(expected, PlacementPanel.ScrolledWidth(width, wheel, fine), 3);
    }

    [Fact]
    public void KeepsTheWidthWithinLimits()
    {
        Assert.Equal(PlacementPanel.PickMinimumWidthMeters, PlacementPanel.ScrolledWidth(0.1f, -5.0f, false));
        Assert.Equal(PlacementPanel.PickMaximumWidthMeters, PlacementPanel.ScrolledWidth(49.0f, 5.0f, false));
        // A width below the minimum still grows from the minimum.
        Assert.True(PlacementPanel.ScrolledWidth(0.0f, 1.0f, false) > PlacementPanel.PickMinimumWidthMeters);
    }

    [Theory]
    [InlineData(1000, 1200, true)]
    [InlineData(1200, 1200, false)]
    [InlineData(1500, 1200, false)]
    public void HoldsTheZoomBrieflyAfterScrolling(long now, long holdUntil, bool holds)
    {
        Assert.Equal(holds, CameraZoomHold.ShouldHold(now, holdUntil));
    }

    [Theory]
    [InlineData(0x00780000u, 1)]
    [InlineData(0xFF880000u, -1)]
    [InlineData(0x00F00000u, 2)]
    public void ReadsTheWheelStepFromTheHookMessage(uint mouseData, int notches)
    {
        Assert.Equal(notches * 120, GameMouseWheelHook.WheelDeltaOf(mouseData));
    }

    [Theory]
    [InlineData(true, 1000, 1100, true)]
    [InlineData(true, 1000, 1300, false)]
    [InlineData(false, 1000, 1100, false)]
    public void GivesTheWheelBackOnceThePluginStopsAsking(bool swallow, long setTick, long now, bool swallowing)
    {
        Assert.Equal(swallowing, GameMouseWheelHook.IsSwallowing(swallow, setTick, now));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    public void CountsAStepWhenTakenNotWhenPeeked(int code, bool counts)
    {
        Assert.Equal(counts, GameMouseWheelHook.ShouldCount(code));
    }

    [Fact]
    public void CancellingPutsTheSizeBack()
    {
        var placement = new ScreenPlacementSettings { WidthMeters = 4.4f, HeightMeters = 2.475f };

        Assert.True(PlacementPanel.RestoreSize(placement, (3.0f, 1.6875f)));
        Assert.Equal(3.0f, placement.WidthMeters);
        Assert.Equal(1.6875f, placement.HeightMeters);

        // Nothing was resized: nothing to put back.
        Assert.False(PlacementPanel.RestoreSize(placement, (3.0f, 1.6875f)));
    }

    [Theory]
    [InlineData(0x00010001L, true)]
    [InlineData(0xC0010001L, false)]
    public void TellsEscapeGoingDownFromItsRelease(long flags, bool down)
    {
        Assert.Equal(down, GameEscapeHook.IsKeyDown((nint)flags));
    }
}
