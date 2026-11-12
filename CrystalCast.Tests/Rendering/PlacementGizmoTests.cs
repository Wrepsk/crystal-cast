using System.Numerics;
using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class PlacementGizmoTests
{
    [Fact]
    public void TheWidthHandlesScaleIsTheNewWidth()
    {
        Assert.Equal(4.0f, ScreenPlacementGizmo.ResizedWidth(4.0f, snap: false)!.Value, 3);
    }

    [Fact]
    public void SnappingLandsOnQuarterMeters()
    {
        Assert.Equal(3.25f, ScreenPlacementGizmo.ResizedWidth(3.2f, snap: true)!.Value, 3);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    public void BrokenScalesChangeNothing(float scale)
    {
        Assert.Null(ScreenPlacementGizmo.ResizedWidth(scale, snap: false));
    }

    [Fact]
    public void ScreensDoNotShrinkBelowTheMinimum()
    {
        Assert.Equal(ScreenPlacementGizmo.MinimumWidthMeters, ScreenPlacementGizmo.ResizedWidth(0.01f, snap: false));
    }

    [Fact]
    public void FaceMePointsTheScreenAtTheViewer()
    {
        // A screen faces along (sin yaw, 0, cos yaw).
        var yaw = ScreenPlacementResolver.FacingYaw(new Vector3(0, 2, 0), new Vector3(3, 0, 3))!.Value;
        var facing = new Vector3(MathF.Sin(yaw), 0.0f, MathF.Cos(yaw));
        Assert.Equal(Vector3.Normalize(new Vector3(3, 0, 3)).X, facing.X, 3);
        Assert.Equal(Vector3.Normalize(new Vector3(3, 0, 3)).Z, facing.Z, 3);
    }

    [Fact]
    public void FaceMeNeedsTheViewerToTheSide()
    {
        Assert.Null(ScreenPlacementResolver.FacingYaw(new Vector3(1, 0, 1), new Vector3(1, 5, 1)));
    }

    [Fact]
    public void ResizeIsAValidSavedGizmoMode()
    {
        var configuration = new Configuration { PlacementGizmoOperation = ScreenPlacementGizmoOperation.Resize };
        configuration.Normalize();
        Assert.Equal(ScreenPlacementGizmoOperation.Resize, configuration.PlacementGizmoOperation);
    }
}
