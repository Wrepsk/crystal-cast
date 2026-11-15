using System.Numerics;
using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class PolygonProjectionTests
{
    private static readonly Vector2 Viewport = new(1920, 1080);

    // A camera at the origin looking down -Z.
    private static readonly Matrix4x4 ViewProjection =
        Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Viewport.X / Viewport.Y, 0.1f, 1000f);

    private static List<Vector2> Project(params Vector3[] corners)
    {
        var output = new List<Vector2>();
        PolygonProjection.Project(corners, ViewProjection, Vector2.Zero, Viewport, output);
        return output;
    }

    private static Vector3[] Square(float halfSize, float z) =>
    [
        new(-halfSize, halfSize, z),
        new(halfSize, halfSize, z),
        new(halfSize, -halfSize, z),
        new(-halfSize, -halfSize, z),
    ];

    [Fact]
    public void ASquareInFrontIsCenteredWithTopUp()
    {
        var outline = Project(Square(1, -5));

        Assert.Equal(4, outline.Count);
        var center = (outline[0] + outline[1] + outline[2] + outline[3]) / 4;
        Assert.True(Vector2.Distance(center, Viewport / 2) < 0.5f, $"center {center}");
        Assert.True(outline[0].Y < outline[3].Y, "the top edge is higher on screen");
    }

    [Fact]
    public void AShapeWiderThanTheViewKeepsAllItsCorners()
    {
        var outline = Project(Square(50, -5));

        Assert.Equal(4, outline.Count);
        Assert.Contains(outline, point => point.X < 0);
        Assert.Contains(outline, point => point.X > Viewport.X);
    }

    [Fact]
    public void ThePartBehindTheCameraIsCutOff()
    {
        // A floor reaching from in front of the camera to behind it.
        var outline = Project(
            new Vector3(-2, -1, -10),
            new Vector3(2, -1, -10),
            new Vector3(2, -1, 10),
            new Vector3(-2, -1, 10));

        Assert.Equal(4, outline.Count);
        Assert.All(outline, point => Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y)));
        // The near edge drops far below the view instead of flipping above it.
        Assert.Contains(outline, point => point.Y > Viewport.Y);
    }

    [Fact]
    public void AShapeFullyBehindTheCameraGivesNothing()
    {
        Assert.Empty(Project(Square(1, 5)));
    }
}
