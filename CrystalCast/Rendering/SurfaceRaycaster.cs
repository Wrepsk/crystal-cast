using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CrystalCast.Rendering;

/// <summary>Finds the game surface (wall, floor, furniture with collision) under a point on the screen.</summary>
internal static class SurfaceRaycaster
{
    /// <summary>
    /// Dalamud's ScreenToWorld finds the spot under the mouse; a raycast from the camera toward it gives the
    /// surface's normal, which ScreenToWorld doesn't report.
    /// </summary>
    public static unsafe bool TryHit(IGameGui gameGui, Vector2 screenPosition, Vector3 cameraOrigin, out Vector3 point, out Vector3 normal)
    {
        point = default;
        normal = default;
        if (!PlacementMath.IsFinite(cameraOrigin)
            || !gameGui.ScreenToWorld(screenPosition, out var target, SurfaceSnap.MaxDistanceMeters)
            || !PlacementMath.IsFinite(target))
        {
            return false;
        }

        var toTarget = target - cameraOrigin;
        var distance = toTarget.Length();
        if (distance < 0.01f)
            return false;

        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null)
            return false;

        var direction = toTarget / distance;
        if (!BGCollisionModule.RaycastMaterialFilter(cameraOrigin, direction, out var hit, distance + 0.5f)
            || !PlacementMath.IsFinite(hit.Point))
        {
            return false;
        }

        if (SurfaceSnap.FacingNormal(hit.Normal, hit.V1, hit.V2, hit.V3, direction) is not { } facing)
            return false;

        point = hit.Point;
        normal = facing;
        return true;
    }

    /// <summary>The first surface along a ray, with its normal turned toward the ray's start.</summary>
    public static unsafe bool TryCast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal)
    {
        point = default;
        normal = default;
        if (!PlacementMath.IsFinite(origin) || !PlacementMath.IsFinite(direction) || direction.LengthSquared() < 0.0001f)
            return false;

        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null)
            return false;

        direction = Vector3.Normalize(direction);
        if (!BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, maxDistance)
            || !PlacementMath.IsFinite(hit.Point)
            || SurfaceSnap.FacingNormal(hit.Normal, hit.V1, hit.V2, hit.V3, direction) is not { } facing)
        {
            return false;
        }

        point = hit.Point;
        normal = facing;
        return true;
    }
}
