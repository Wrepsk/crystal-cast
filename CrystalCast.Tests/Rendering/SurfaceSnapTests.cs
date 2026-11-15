using System.Numerics;
using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class SurfaceSnapTests
{
    private static Vector3 Facing(SurfaceSnapPose pose) => Vector3.Transform(Vector3.UnitZ, pose.Rotation);

    private static Vector3 Up(SurfaceSnapPose pose) => Vector3.Transform(Vector3.UnitY, pose.Rotation);

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 0.001f, $"expected {expected}, got {actual}");
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 0f, -1f)]
    [InlineData(-0.6f, 0f, 0.8f)]
    [InlineData(0f, 0.5f, 0.8660254f)] // leaning back
    public void AScreenOnAWallFacesOutOfIt(float x, float y, float z)
    {
        var normal = Vector3.Normalize(new Vector3(x, y, z));
        var pose = SurfaceSnap.Pose(new Vector3(10, 2, 5), normal, viewer: new Vector3(10, 2, 5) + (normal * 4));

        AssertClose(normal, Facing(pose));
        AssertClose(new Vector3(10, 2, 5) + (normal * SurfaceSnap.DefaultGapMeters), pose.Position);
    }

    [Fact]
    public void AScreenOnAWallStaysUpright()
    {
        var pose = SurfaceSnap.Pose(Vector3.Zero, Vector3.UnitX, viewer: new Vector3(5, 0, 0));

        AssertClose(Vector3.UnitY, Up(pose));
    }

    [Fact]
    public void AScreenOnTheFloorLiesFlatAndReadsUprightFromTheViewer()
    {
        var pose = SurfaceSnap.Pose(Vector3.Zero, Vector3.UnitY, viewer: new Vector3(0, 1.6f, -3));

        AssertClose(Vector3.UnitY, Facing(pose));
        // Its top edge points away from the viewer, so the picture isn't upside down.
        Assert.True(Up(pose).Z > 0.99f, $"up {Up(pose)}");
    }

    [Fact]
    public void TheNormalIsTurnedTowardTheRay()
    {
        var normal = SurfaceSnap.FacingNormal(new Vector3(0, 0, 1), default, default, default, rayDirection: new Vector3(0, 0, 1));

        Assert.NotNull(normal);
        AssertClose(new Vector3(0, 0, -1), normal.Value);
    }

    [Fact]
    public void AMissingNormalFallsBackToTheTriangle()
    {
        var normal = SurfaceSnap.FacingNormal(
            Vector3.Zero,
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
            rayDirection: new Vector3(0, 0, -1));

        Assert.NotNull(normal);
        AssertClose(new Vector3(0, 0, 1), normal.Value);
    }

    [Fact]
    public void NoNormalAndAFlatTriangleGiveNothing()
    {
        Assert.Null(SurfaceSnap.FacingNormal(Vector3.Zero, Vector3.One, Vector3.One, Vector3.One, Vector3.UnitZ));
    }
}
