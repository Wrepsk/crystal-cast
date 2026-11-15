using System.Numerics;
using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class FurnitureFaceFinderTests
{
    /// <summary>
    /// A flat panel facing +Z at depth <paramref name="faceZ"/>, spanning the given X and Y ranges, optionally with a
    /// wall behind it that fills everything.
    /// </summary>
    private static Func<Vector3, Vector3, float, RayHit?> Panel(
        float faceZ, float minX, float maxX, float minY, float maxY, float? wallZ = null, bool colliders = true)
    {
        // The panel and the wall are separate colliders, as an item and the house are in the game.
        nint panelCollider = colliders ? 1 : 0;
        nint wallCollider = colliders ? 2 : 0;
        return (origin, direction, maxDistance) =>
        {
            // The finder only casts level rays straight back along -Z here.
            Assert.True(MathF.Abs(direction.Z + 1) < 0.0001f, $"direction {direction}");
            if (origin.X >= minX && origin.X <= maxX && origin.Y >= minY && origin.Y <= maxY && origin.Z - faceZ <= maxDistance)
                return new RayHit(origin with { Z = faceZ }, Vector3.UnitZ, panelCollider);

            if (wallZ is { } wall && origin.Z - wall <= maxDistance)
                return new RayHit(origin with { Z = wall }, Vector3.UnitZ, wallCollider);

            return null;
        };
    }

    [Fact]
    public void CentersOnATallPanel()
    {
        // A stage panel 4 m tall and 2.4 m wide standing on its base.
        var face = FurnitureFaceFinder.Find(Vector3.Zero, 0.0f, Panel(0.1f, -1.2f, 1.2f, 0.0f, 4.0f));

        Assert.NotNull(face);
        Assert.InRange(face.Value.Center.Y, 1.95f, 2.05f);
        Assert.InRange(face.Value.Center.X, -0.05f, 0.05f);
        Assert.InRange(face.Value.Center.Z, 0.09f, 0.11f);
        Assert.InRange(face.Value.Height, 3.9f, 4.1f);
        Assert.InRange(face.Value.Width, 2.3f, 2.5f);
    }

    [Fact]
    public void CentersOnAFaceOffToOneSideOfTheBase()
    {
        // A wide, low item whose base sits near its left end.
        var face = FurnitureFaceFinder.Find(Vector3.Zero, 0.0f, Panel(0.1f, -0.5f, 2.5f, 0.2f, 1.2f, wallZ: -0.5f));

        Assert.NotNull(face);
        Assert.InRange(face.Value.Center.X, 0.95f, 1.05f);
        Assert.InRange(face.Value.Center.Y, 0.65f, 0.75f);
    }

    [Fact]
    public void StopsAtTheEdgesEvenWithAWallBehind()
    {
        // The wall is deeper than the face, so rays past the panel's edges no longer count.
        var face = FurnitureFaceFinder.Find(Vector3.Zero, 0.0f, Panel(0.1f, -1.0f, 1.0f, 0.5f, 2.5f, wallZ: -0.6f));

        Assert.NotNull(face);
        Assert.InRange(face.Value.Center.Y, 1.45f, 1.55f);
        Assert.InRange(face.Value.Height, 1.9f, 2.1f);
    }

    [Fact]
    public void APanelStandingAgainstAWallIsMeasuredByItsOwnCollider()
    {
        // The wall is only 5 cm behind the panel's front, well inside the depth tolerance.
        var face = FurnitureFaceFinder.Find(Vector3.Zero, 0.0f, Panel(0.1f, -1.2f, 1.2f, 0.0f, 4.0f, wallZ: 0.05f));

        Assert.NotNull(face);
        Assert.InRange(face.Value.Center.Y, 1.95f, 2.05f);
        Assert.InRange(face.Value.Width, 2.3f, 2.5f);
    }

    [Fact]
    public void WithoutColliderIdsTheDepthStillSeparatesAFaceFromADeeperWall()
    {
        var face = FurnitureFaceFinder.Find(Vector3.Zero, 0.0f, Panel(0.1f, -1.0f, 1.0f, 0.5f, 2.5f, wallZ: -0.6f, colliders: false));

        Assert.NotNull(face);
        Assert.InRange(face.Value.Center.Y, 1.45f, 1.55f);
    }

    [Fact]
    public void AnItemWithoutCollisionDoesNotMeasureTheWallBehindIt()
    {
        var onlyWall = Panel(faceZ: 0.0f, minX: 0, maxX: 0, minY: -1, maxY: -1, wallZ: -3.0f);

        Assert.Null(FurnitureFaceFinder.Find(Vector3.Zero, 0.0f, onlyWall));
    }

    [Fact]
    public void AWallCloseBehindAnItemWithoutCollisionIsNotAFace()
    {
        var onlyWall = Panel(faceZ: 0.0f, minX: 0, maxX: 0, minY: -1, maxY: -1, wallZ: -0.4f);

        Assert.Null(FurnitureFaceFinder.Find(Vector3.Zero, 0.0f, onlyWall));
    }

    [Fact]
    public void FollowsTheItemsTurn()
    {
        // Turned a quarter: the front faces +X. Rotate the ray into the panel's frame and back.
        var panel = Panel(0.1f, -1.0f, 1.0f, 0.0f, 3.0f);
        var toLocal = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        var toWorld = Quaternion.Inverse(toLocal);
        RayHit? Cast(Vector3 origin, Vector3 direction, float maxDistance) =>
            panel(Vector3.Transform(origin, toLocal), Vector3.Transform(direction, toLocal), maxDistance) is { } hit
                ? new RayHit(Vector3.Transform(hit.Point, toWorld), Vector3.Transform(hit.Normal, toWorld))
                : null;

        var face = FurnitureFaceFinder.Find(Vector3.Zero, MathF.PI / 2, Cast);

        Assert.NotNull(face);
        Assert.InRange(face.Value.Center.X, 0.09f, 0.11f);
        Assert.InRange(face.Value.Center.Y, 1.45f, 1.55f);
        Assert.True(Vector3.Distance(Vector3.UnitX, face.Value.Normal) < 0.001f, $"normal {face.Value.Normal}");
    }
}
