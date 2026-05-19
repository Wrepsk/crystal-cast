using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace CrystalCast.Rendering;

public readonly record struct ResolvedScreenPlacement(
    Vector3 Position,
    float YawRadians,
    float PitchRadians,
    float RollRadians)
{
    public Quaternion Rotation => Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(YawRadians, PitchRadians, RollRadians));
}

public static class ScreenPlacementResolver
{
    public static bool TryResolve(ScreenPlacementSettings placement, out ResolvedScreenPlacement resolved)
    {
        if (placement.Mode != ScreenPlacementMode.World)
        {
            if (!TryGetPlacementFrame(placement.Mode, out var framePosition, out var frameYaw, out var forward, out var right))
            {
                resolved = default;
                return false;
            }

            resolved = new ResolvedScreenPlacement(
                framePosition
                    + (right * placement.PositionX)
                    + (Vector3.UnitY * placement.PositionY)
                    + (forward * placement.PositionZ),
                frameYaw + placement.YawRadians,
                placement.PitchRadians,
                placement.RollRadians);
            return true;
        }

        resolved = new ResolvedScreenPlacement(
            new Vector3(placement.PositionX, placement.PositionY, placement.PositionZ),
            placement.YawRadians,
            placement.PitchRadians,
            placement.RollRadians);
        return true;
    }

    public static bool TryConvertModePreservingWorld(ScreenPlacementSettings placement, ScreenPlacementMode targetMode)
    {
        if (placement.Mode == targetMode)
            return true;

        if (!TryResolve(placement, out var worldPlacement))
            return false;

        if (targetMode == ScreenPlacementMode.World)
        {
            placement.Mode = ScreenPlacementMode.World;
            ApplyWorldPlacement(placement, worldPlacement);
            return true;
        }

        if (!TryGetPlacementFrame(targetMode, out var framePosition, out var frameYaw, out var forward, out var right))
            return false;

        var offset = worldPlacement.Position - framePosition;
        placement.Mode = targetMode;
        placement.PositionX = Vector3.Dot(offset, right);
        placement.PositionY = offset.Y;
        placement.PositionZ = Vector3.Dot(offset, forward);
        placement.YawRadians = NormalizeRadians(worldPlacement.YawRadians - frameYaw);
        placement.PitchRadians = worldPlacement.PitchRadians;
        placement.RollRadians = worldPlacement.RollRadians;
        placement.Normalize();
        return true;
    }

    public static bool PlaceInFrontOfPlayer(ScreenPlacementSettings placement, float distanceMeters = 3.0f)
    {
        if (placement.Mode != ScreenPlacementMode.World)
        {
            if (!TryGetPlacementFrame(placement.Mode, out _, out _, out _, out _))
                return false;

            placement.PositionX = 0.0f;
            placement.PositionY = 1.4f;
            placement.PositionZ = distanceMeters;
            placement.YawRadians = MathF.PI;
            placement.PitchRadians = 0.0f;
            placement.RollRadians = 0.0f;
            return true;
        }

        if (!TryGetPlayerFrame(out var playerPosition, out var playerYaw, out var forward, out _))
            return false;

        var center = playerPosition + (forward * distanceMeters) + (Vector3.UnitY * 1.4f);
        placement.PositionX = center.X;
        placement.PositionY = center.Y;
        placement.PositionZ = center.Z;
        placement.YawRadians = playerYaw + MathF.PI;
        placement.PitchRadians = 0.0f;
        placement.RollRadians = 0.0f;
        return true;
    }

    private static void ApplyWorldPlacement(ScreenPlacementSettings placement, ResolvedScreenPlacement worldPlacement)
    {
        placement.PositionX = worldPlacement.Position.X;
        placement.PositionY = worldPlacement.Position.Y;
        placement.PositionZ = worldPlacement.Position.Z;
        placement.YawRadians = worldPlacement.YawRadians;
        placement.PitchRadians = worldPlacement.PitchRadians;
        placement.RollRadians = worldPlacement.RollRadians;
        placement.Normalize();
    }

    private static bool TryGetPlacementFrame(ScreenPlacementMode mode, out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        return mode switch
        {
            ScreenPlacementMode.FollowPlayer => TryGetPlayerFrame(out position, out yaw, out forward, out right),
            ScreenPlacementMode.FollowCamera => TryGetCameraFrame(out position, out yaw, out forward, out right),
            _ => ClearFrame(out position, out yaw, out forward, out right),
        };
    }

    private static bool TryGetPlayerFrame(out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return ClearFrame(out position, out yaw, out forward, out right);

        position = player.Position;
        yaw = player.Rotation;
        BuildHorizontalAxes(yaw, out forward, out right);
        return true;
    }

    private static unsafe bool TryGetCameraFrame(out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return ClearFrame(out position, out yaw, out forward, out right);

        var camera = Control.Instance()->CameraManager.GetActiveCamera();
        if (camera == null)
            return ClearFrame(out position, out yaw, out forward, out right);

        position = player.Position;
        var renderCamera = camera->SceneCamera.RenderCamera;
        if (renderCamera != null)
        {
            var cameraOrigin = (Vector3)renderCamera->Origin;
            var cameraToPlayer = position - cameraOrigin;
            cameraToPlayer.Y = 0.0f;
            if (cameraToPlayer.LengthSquared() > 0.0001f)
            {
                forward = Vector3.Normalize(cameraToPlayer);
                right = new Vector3(forward.Z, 0.0f, -forward.X);
                yaw = MathF.Atan2(forward.X, forward.Z);
                return true;
            }
        }

        yaw = camera->CalculateSceneCameraYaw();
        if (!float.IsFinite(yaw))
            return ClearFrame(out position, out yaw, out forward, out right);

        BuildHorizontalAxes(yaw, out forward, out right);
        return true;
    }

    private static void BuildHorizontalAxes(float yaw, out Vector3 forward, out Vector3 right)
    {
        forward = new Vector3(MathF.Sin(yaw), 0.0f, MathF.Cos(yaw));
        right = new Vector3(MathF.Cos(yaw), 0.0f, -MathF.Sin(yaw));
    }

    private static bool ClearFrame(out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        position = default;
        yaw = 0.0f;
        forward = default;
        right = default;
        return false;
    }

    private static float NormalizeRadians(float value)
    {
        while (value <= -MathF.PI)
            value += MathF.Tau;
        while (value > MathF.PI)
            value -= MathF.Tau;
        return value;
    }
}
