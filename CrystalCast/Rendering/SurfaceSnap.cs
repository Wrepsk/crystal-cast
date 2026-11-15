using System.Numerics;

namespace CrystalCast.Rendering;

/// <summary>Where a screen snapped to a surface goes: its center and the turn that lays it flat on the surface.</summary>
internal readonly record struct SurfaceSnapPose(Vector3 Position, float YawRadians, float PitchRadians)
{
    public Quaternion Rotation => Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(YawRadians, PitchRadians, 0.0f));
}

/// <summary>Turns a collision hit into a screen pose that sits flat against the surface, facing out of it.</summary>
internal static class SurfaceSnap
{
    /// <summary>
    /// How far in front of the surface the screen sits by default. Collision shapes can be a few centimeters off
    /// the visible surface, and screens are hidden once they're 2 cm behind the game's depth, so a few centimeters
    /// isn't enough: parts of the screen dip into the wall as the camera moves.
    /// </summary>
    public const float DefaultGapMeters = 0.1f;

    public const float MaxGapMeters = 1.0f;

    /// <summary>The farthest surface that can be picked.</summary>
    public const float MaxDistanceMeters = 100.0f;

    /// <summary>Below this much sideways lean a surface counts as a floor or ceiling, and the screen turns to the viewer.</summary>
    private const float FlatSurfaceHorizontalLength = 0.05f;

    /// <summary>
    /// The surface's unit normal on the side the ray came from. Uses the reported normal, or the hit triangle's
    /// when the reported one is missing. Null when neither gives a direction.
    /// </summary>
    public static Vector3? FacingNormal(Vector3 reportedNormal, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 rayDirection)
    {
        var normal = reportedNormal;
        if (!PlacementMath.IsFinite(normal) || normal.LengthSquared() < 0.25f)
            normal = Vector3.Cross(v2 - v1, v3 - v1);

        if (!PlacementMath.IsFinite(normal) || normal.LengthSquared() < 1e-10f)
            return null;

        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, rayDirection) > 0.0f ? -normal : normal;
    }

    /// <summary>
    /// The pose for a screen centered on <paramref name="point"/>, facing along <paramref name="normal"/>. A screen
    /// faces along its rotated +Z: (sin yaw cos pitch, -sin pitch, cos yaw cos pitch). On a floor or ceiling the
    /// normal gives no yaw, so the screen turns toward <paramref name="viewer"/> and reads upright from there.
    /// </summary>
    public static SurfaceSnapPose Pose(Vector3 point, Vector3 normal, Vector3 viewer, float gapMeters = DefaultGapMeters)
    {
        normal = Vector3.Normalize(normal);
        var horizontalLength = MathF.Sqrt((normal.X * normal.X) + (normal.Z * normal.Z));
        var yaw = horizontalLength > FlatSurfaceHorizontalLength
            ? MathF.Atan2(normal.X, normal.Z)
            : ScreenPlacementResolver.FacingYaw(point, viewer) ?? 0.0f;
        var pitch = MathF.Asin(Math.Clamp(-normal.Y, -1.0f, 1.0f));
        var gap = float.IsFinite(gapMeters) ? Math.Clamp(gapMeters, 0.0f, MaxGapMeters) : DefaultGapMeters;
        return new SurfaceSnapPose(point + (normal * gap), yaw, pitch);
    }
}
