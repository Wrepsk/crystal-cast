using System.Numerics;

namespace CrystalCast.Rendering;

/// <summary>A surface hit: where, the normal facing back along the ray, and the collider it hit (0 when unknown).</summary>
internal readonly record struct RayHit(Vector3 Point, Vector3 Normal, nint Collider = 0);

/// <summary>The front face of a housing item, measured with rays.</summary>
internal readonly record struct FurnitureFace(Vector3 Center, Vector3 Normal, float Width, float Height);

/// <summary>
/// Finds the middle of a housing item's front face. The game's own center point for housing items sits a fixed
/// height above their base, so a tall item would get a screen low on its face. Instead, level rays are cast at the
/// item from in front, stepping up, down, left and right from a first hit while they keep landing on the same
/// face: the same collider at about the same depth. Where they stop are the face's edges. The collider matters for
/// an item standing against a wall, whose front is only a few centimeters in front of the wall.
/// </summary>
internal static class FurnitureFaceFinder
{
    /// <summary>How far in front of the item the rays start.</summary>
    public const float RayStartMeters = 3.0f;

    /// <summary>Distance between rays when stepping along the face.</summary>
    public const float StepMeters = 0.05f;

    /// <summary>How far the face is measured in each direction from the first hit.</summary>
    public const float MaxReachMeters = 8.0f;

    /// <summary>How much deeper or shallower a hit may be and still count as the same face (frames, bevels).</summary>
    public const float DepthToleranceMeters = 0.2f;

    /// <summary>
    /// The farthest a first hit may be in front of or behind the item's base. Anything farther is something else,
    /// like the wall behind an item without collision.
    /// </summary>
    public const float MaxFaceDepthMeters = 2.0f;

    /// <summary>Heights above the base tried for the first hit, in order.</summary>
    private static readonly float[] SeedHeights = [1.5f, 1.0f, 0.5f, 2.0f, 2.5f, 3.0f, 0.25f];

    /// <param name="basePosition">The item's position (its base).</param>
    /// <param name="yawRadians">The item's turn; its front faces along (sin yaw, 0, cos yaw).</param>
    /// <param name="cast">Casts a ray (origin, unit direction, max distance) and returns the first hit.</param>
    public static FurnitureFace? Find(Vector3 basePosition, float yawRadians, Func<Vector3, Vector3, float, RayHit?> cast)
    {
        var forward = new Vector3(MathF.Sin(yawRadians), 0.0f, MathF.Cos(yawRadians));
        var right = new Vector3(MathF.Cos(yawRadians), 0.0f, -MathF.Sin(yawRadians));
        var maxDistance = RayStartMeters * 2.0f;

        RayHit? HitAt(float side, float height) =>
            cast(basePosition + (forward * RayStartMeters) + (right * side) + (Vector3.UnitY * height), -forward, maxDistance);

        foreach (var seedHeight in SeedHeights)
        {
            var seedOrigin = basePosition + (forward * RayStartMeters) + (Vector3.UnitY * seedHeight);
            if (cast(seedOrigin, -forward, maxDistance) is not { } seed)
                continue;

            var depth = Vector3.Dot(seed.Point - basePosition, forward);
            if (MathF.Abs(depth) > MaxFaceDepthMeters)
                continue;

            bool OnFace(float side, float height) =>
                HitAt(side, height) is { } hit
                && (seed.Collider == 0 || hit.Collider == seed.Collider)
                && MathF.Abs(Vector3.Dot(hit.Point - basePosition, forward) - depth) <= DepthToleranceMeters;

            var top = Reach(height => OnFace(0.0f, seedHeight + height));
            var bottom = Reach(height => OnFace(0.0f, seedHeight - height));
            var centerHeight = seedHeight + ((top - bottom) * 0.5f);

            var left = Reach(side => OnFace(-side, centerHeight));
            var rightReach = Reach(side => OnFace(side, centerHeight));
            var centerSide = (rightReach - left) * 0.5f;

            // Running out of reach means a wall or floor-sized surface, not a piece of furniture; a lower or
            // higher first ray may still find the item (a low item under the first ray, a wall behind it).
            if (top >= MaxReachMeters - StepMeters || bottom >= MaxReachMeters - StepMeters
                || left >= MaxReachMeters - StepMeters || rightReach >= MaxReachMeters - StepMeters)
            {
                continue;
            }

            var center = basePosition + (forward * depth) + (right * centerSide) + (Vector3.UnitY * centerHeight);
            var normal = cast(basePosition + (forward * RayStartMeters) + (right * centerSide) + (Vector3.UnitY * centerHeight), -forward, maxDistance)?.Normal
                ?? seed.Normal;
            return new FurnitureFace(center, normal, left + rightReach + StepMeters, top + bottom + StepMeters);
        }

        return null;
    }

    /// <summary>How far the face goes from the first hit: the last step that still landed on it.</summary>
    private static float Reach(Func<float, bool> onFace)
    {
        var reach = 0.0f;
        for (var step = StepMeters; step <= MaxReachMeters; step += StepMeters)
        {
            if (!onFace(step))
                break;
            reach = step;
        }

        return reach;
    }
}
