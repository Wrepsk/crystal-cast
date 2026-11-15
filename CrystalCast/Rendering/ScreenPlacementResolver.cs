using System.Diagnostics;
using System.Numerics;
using CrystalCast.Rooms;
using Dalamud.Plugin.Services;
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

internal sealed class ScreenPlacementResolver(IObjectTable objectTable, IClientState clientState)
{
    private const float MaxRenderPositionOffsetMeters = 10.0f;
    private const float MaxRenderYawDifferenceRadians = MathF.PI * 0.5f;

    private readonly FollowFramePredictor playerPrediction = new();
    private readonly FollowFramePredictor cameraPrediction = new();
    private readonly PlacementPredictionContext predictionContext = new();
    private readonly object predictionGate = new();

    // Housing items are read at most once per frame or so, however many screens are attached.
    private const long FurnitureCacheMilliseconds = 10;
    private readonly object furnitureGate = new();
    private readonly List<HousingFurnitureItem> furnitureCache = [];
    private RoomLocation furnitureCacheLocation = new();
    private long furnitureCacheReadAtMs;

    public bool TryResolve(ScreenPlacementSettings placement, out ResolvedScreenPlacement resolved)
    {
        return TryResolve(placement, 0.0f, applyFollowPrediction: false, out resolved);
    }

    public bool TryResolve(ScreenPlacementSettings placement, float predictionSeconds, out ResolvedScreenPlacement resolved)
    {
        return TryResolve(placement, predictionSeconds, applyFollowPrediction: true, out resolved);
    }

    private bool TryResolve(
        ScreenPlacementSettings placement,
        float predictionSeconds,
        bool applyFollowPrediction,
        out ResolvedScreenPlacement resolved)
    {
        if (placement.Mode != ScreenPlacementMode.World)
        {
            if (!TryGetPlacementFrame(placement.Mode, placement.Furniture, out var framePosition, out var frameYaw, out var forward, out var right))
            {
                resolved = default;
                return false;
            }

            if (applyFollowPrediction)
                ApplyFramePrediction(placement.Mode, ref framePosition, ref frameYaw, predictionSeconds);
            BuildHorizontalAxes(frameYaw, out forward, out right);
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

    public bool TryConvertModePreservingWorld(ScreenPlacementSettings placement, ScreenPlacementMode targetMode)
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

        // Attaching needs an item, which TryAttachToFurniture takes.
        if (targetMode == ScreenPlacementMode.Furniture
            || !TryGetPlacementFrame(targetMode, null, out var framePosition, out var frameYaw, out var forward, out var right))
        {
            return false;
        }

        var offset = worldPlacement.Position - framePosition;
        placement.Mode = targetMode;
        placement.PositionX = Vector3.Dot(offset, right);
        placement.PositionY = offset.Y;
        placement.PositionZ = Vector3.Dot(offset, forward);
        placement.YawRadians = PlacementMath.NormalizeRadians(worldPlacement.YawRadians - frameYaw);
        placement.PitchRadians = worldPlacement.PitchRadians;
        placement.RollRadians = worldPlacement.RollRadians;
        placement.Normalize();
        return true;
    }

    public bool TryApplyWorldPositionPreservingMode(ScreenPlacementSettings placement, Vector3 worldPosition)
    {
        if (!PlacementMath.IsFinite(worldPosition))
            return false;

        if (placement.Mode == ScreenPlacementMode.World)
        {
            placement.PositionX = worldPosition.X;
            placement.PositionY = worldPosition.Y;
            placement.PositionZ = worldPosition.Z;
            placement.Normalize();
            return true;
        }

        if (!TryGetPlacementFrame(placement.Mode, placement.Furniture, out var framePosition, out _, out var forward, out var right))
            return false;

        var offset = worldPosition - framePosition;
        placement.PositionX = Vector3.Dot(offset, right);
        placement.PositionY = offset.Y;
        placement.PositionZ = Vector3.Dot(offset, forward);
        placement.Normalize();
        return true;
    }

    public bool TryApplyWorldRotationPreservingMode(ScreenPlacementSettings placement, Quaternion worldRotation)
    {
        if (!TryGetYawPitchRoll(worldRotation, out var yawRadians, out var pitchRadians, out var rollRadians))
            return false;

        if (placement.Mode != ScreenPlacementMode.World)
        {
            if (!TryGetPlacementFrame(placement.Mode, placement.Furniture, out _, out var frameYaw, out _, out _))
                return false;

            yawRadians = PlacementMath.NormalizeRadians(yawRadians - frameYaw);
        }

        placement.YawRadians = PlacementMath.NormalizeRadians(yawRadians);
        placement.PitchRadians = PlacementMath.NormalizeRadians(pitchRadians);
        placement.RollRadians = PlacementMath.NormalizeRadians(rollRadians);
        placement.Normalize();
        return true;
    }

    public bool PlaceInFrontOfPlayer(ScreenPlacementSettings placement, float distanceMeters = 3.0f)
    {
        if (placement.Mode == ScreenPlacementMode.Furniture)
        {
            // Placed in the world in front of the player, then kept attached through the item's frame.
            if (!TryGetPlayerFrame(out var standingAt, out var facingYaw, out var playerForward, out _))
                return false;

            return TryApplyWorldPositionPreservingMode(placement, standingAt + (playerForward * distanceMeters) + (Vector3.UnitY * 1.4f))
                && TryApplyWorldRotationPreservingMode(placement, Quaternion.CreateFromYawPitchRoll(facingYaw + MathF.PI, 0.0f, 0.0f));
        }

        if (placement.Mode != ScreenPlacementMode.World)
        {
            if (!TryGetPlacementFrame(placement.Mode, null, out _, out _, out _, out _))
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

    /// <summary>Turns the screen to face the camera and levels it (no tilt or roll), keeping its position.</summary>
    public bool FaceCamera(ScreenPlacementSettings placement)
    {
        if (!TryResolve(placement, out var resolved) || !TryGetViewerPosition(out var viewer))
            return false;

        return FacingYaw(resolved.Position, viewer) is { } yaw
            && TryApplyWorldRotationPreservingMode(placement, Quaternion.CreateFromYawPitchRoll(yaw, 0.0f, 0.0f));
    }

    /// <summary>
    /// The yaw that points a screen at <paramref name="screen"/> toward <paramref name="viewer"/>, ignoring
    /// height. A screen faces along (sin yaw, 0, cos yaw). Null when the viewer is straight above or below.
    /// </summary>
    internal static float? FacingYaw(Vector3 screen, Vector3 viewer)
    {
        var toViewer = viewer - screen;
        toViewer.Y = 0.0f;
        return toViewer.LengthSquared() < 0.0001f ? null : MathF.Atan2(toViewer.X, toViewer.Z);
    }

    /// <summary>Where the local player stands.</summary>
    public bool TryGetPlayerPosition(out Vector3 position)
    {
        position = objectTable.LocalPlayer?.Position ?? default;
        return objectTable.LocalPlayer != null && PlacementMath.IsFinite(position);
    }

    /// <summary>The camera's position, or the player's when the camera can't be read.</summary>
    internal unsafe bool TryGetViewerPosition(out Vector3 viewer)
    {
        var control = Control.Instance();
        var camera = control != null ? control->CameraManager.GetActiveCamera() : null;
        var renderCamera = camera != null ? camera->SceneCamera.RenderCamera : null;
        if (renderCamera != null)
        {
            viewer = TryGetCurrentCameraOrigin(control->ViewProjectionMatrix, renderCamera->ProjectionMatrix, out var origin)
                ? origin
                : (Vector3)renderCamera->Origin;
            if (PlacementMath.IsFinite(viewer))
                return true;
        }

        viewer = objectTable.LocalPlayer?.Position ?? default;
        return objectTable.LocalPlayer != null && PlacementMath.IsFinite(viewer);
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

    private bool TryGetPlacementFrame(
        ScreenPlacementMode mode,
        FurnitureAnchor? anchor,
        out Vector3 position,
        out float yaw,
        out Vector3 forward,
        out Vector3 right)
    {
        return mode switch
        {
            ScreenPlacementMode.FollowPlayer => TryGetPlayerFrame(out position, out yaw, out forward, out right),
            ScreenPlacementMode.FollowCamera => TryGetCameraFrame(out position, out yaw, out forward, out right),
            ScreenPlacementMode.Furniture when anchor != null => TryGetFurnitureFrame(anchor, out position, out yaw, out forward, out right),
            _ => ClearFrame(out position, out yaw, out forward, out right),
        };
    }

    private bool TryGetFurnitureFrame(FurnitureAnchor anchor, out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        if (!TryFindFurniture(anchor, out var item))
            return ClearFrame(out position, out yaw, out forward, out right);

        position = item.Position;
        yaw = item.YawRadians;
        BuildHorizontalAxes(yaw, out forward, out right);
        return true;
    }

    /// <summary>Where the player is, as rooms describe it: world, zone, and in housing the ward half and house.</summary>
    public RoomLocation ReadLocation() => GameRoomLocation.Read(clientState, objectTable);

    /// <summary>The housing items placed here, with their names, for picking one.</summary>
    public List<HousingFurnitureItem> ReadFurnitureWithNames()
    {
        var items = new List<HousingFurnitureItem>();
        HousingFurnitureReader.Read(items, withNames: true);
        return items;
    }

    /// <summary>The item an anchor points at, if it's placed where the player is.</summary>
    public bool TryFindFurniture(FurnitureAnchor anchor, out HousingFurnitureItem item)
    {
        lock (furnitureGate)
        {
            var now = Environment.TickCount64;
            if (FurnitureCacheExpired(furnitureCacheReadAtMs, now))
            {
                furnitureCacheLocation = ReadLocation();
                HousingFurnitureReader.Read(furnitureCache, withNames: false);
                furnitureCacheReadAtMs = now;
            }

            if (FurnitureAnchorMatcher.Find(anchor, furnitureCacheLocation, furnitureCache) is { } found)
            {
                item = found;
                return true;
            }
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Attaches the screen to <paramref name="item"/> and moves it onto the middle of the item's front face, measured
    /// with rays (<see cref="FurnitureFaceFinder"/>). With nothing to hit (an item without collision), it goes just in
    /// front of the game's center point for the item, facing the same way.
    /// </summary>
    public bool TryMoveOntoFurniture(ScreenPlacementSettings placement, RoomLocation house, HousingFurnitureItem item, float gapMeters)
    {
        if (!house.IsKnown || !house.InHousing)
            return false;

        var pose = FurnitureFrontPose(item, gapMeters);
        var before = placement.Clone();
        placement.Mode = ScreenPlacementMode.Furniture;
        placement.Furniture = FurnitureAnchor.For(house, item);
        placement.Normalize();
        if (TryApplyWorldPositionPreservingMode(placement, pose.Position)
            && TryApplyWorldRotationPreservingMode(placement, pose.Rotation))
        {
            return true;
        }

        placement.CopyFrom(before);
        return false;
    }

    /// <summary>Where a screen moved onto <paramref name="item"/> would go: the middle of its front face, or in front of its center.</summary>
    public static SurfaceSnapPose FurnitureFrontPose(HousingFurnitureItem item, float gapMeters)
    {
        var center = item.Center == default ? item.Position + Vector3.UnitY : item.Center;
        BuildHorizontalAxes(item.YawRadians, out var forward, out _);
        var face = FurnitureFaceFinder.Find(item.Position, item.YawRadians, static (origin, direction, maxDistance) =>
            SurfaceRaycaster.TryCast(origin, direction, maxDistance, out RayHit hit) ? hit : null);
        return face is { } found
            ? SurfaceSnap.Pose(found.Center, found.Normal, found.Center + (forward * FurnitureFaceFinder.RayStartMeters), gapMeters)
            : new SurfaceSnapPose(center + (forward * gapMeters), item.YawRadians, 0.0f);
    }

    /// <summary>Whether the cached housing items are too old. Zero means never read.</summary>
    internal static bool FurnitureCacheExpired(long readAtMs, long nowMs) =>
        readAtMs == 0 || nowMs < readAtMs || nowMs - readAtMs > FurnitureCacheMilliseconds;

    /// <summary>
    /// Attaches the screen to <paramref name="item"/> without moving it: its current world position and turn become
    /// offsets in the item's frame, so moving or turning the item carries the screen along. A screen attached to an
    /// item that isn't found here can't say where it is, so it moves to the new item keeping its offset.
    /// </summary>
    public bool TryAttachToFurniture(ScreenPlacementSettings placement, RoomLocation house, HousingFurnitureItem item)
    {
        if (!house.IsKnown || !house.InHousing)
            return false;

        if (!TryResolve(placement, out var world))
        {
            if (placement.Mode != ScreenPlacementMode.Furniture)
                return false;

            placement.Furniture = FurnitureAnchor.For(house, item);
            placement.Normalize();
            return true;
        }

        BuildHorizontalAxes(item.YawRadians, out var forward, out var right);
        var offset = world.Position - item.Position;
        placement.Mode = ScreenPlacementMode.Furniture;
        placement.Furniture = FurnitureAnchor.For(house, item);
        placement.PositionX = Vector3.Dot(offset, right);
        placement.PositionY = offset.Y;
        placement.PositionZ = Vector3.Dot(offset, forward);
        placement.YawRadians = PlacementMath.NormalizeRadians(world.YawRadians - item.YawRadians);
        placement.PitchRadians = world.PitchRadians;
        placement.RollRadians = world.RollRadians;
        placement.Normalize();
        return true;
    }

    private bool TryGetPlayerFrame(out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        var player = objectTable.LocalPlayer;
        RefreshPredictionContext(player?.Address ?? nint.Zero);
        if (player == null)
            return ClearFrame(out position, out yaw, out forward, out right);

        position = player.Position;
        yaw = player.Rotation;
        if (!PlacementMath.IsFinite(position) || !float.IsFinite(yaw))
            return ClearFrame(out position, out yaw, out forward, out right);

        TryApplyPlayerRenderTransform(player.Address, ref position, ref yaw);

        BuildHorizontalAxes(yaw, out forward, out right);
        return true;
    }

    private static unsafe void TryApplyPlayerRenderTransform(nint playerAddress, ref Vector3 position, ref float yaw)
    {
        if (playerAddress == nint.Zero)
            return;

        var nativePlayer = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)playerAddress;
        var drawObject = nativePlayer->DrawObject;
        if (drawObject == null)
            return;

        var renderPosition = (Vector3)drawObject->Position;
        if (!PlacementMath.IsFinite(renderPosition)
            || Vector3.DistanceSquared(renderPosition, position) > MaxRenderPositionOffsetMeters * MaxRenderPositionOffsetMeters)
        {
            return;
        }

        position = renderPosition;
        var renderRotation = (Quaternion)drawObject->Rotation;
        if (!TryGetYawPitchRoll(renderRotation, out var renderYaw, out _, out _)
            || MathF.Abs(PlacementMath.NormalizeRadians(renderYaw - yaw)) > MaxRenderYawDifferenceRadians)
        {
            return;
        }

        yaw = renderYaw;
    }

    private unsafe bool TryGetCameraFrame(out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        var player = objectTable.LocalPlayer;
        RefreshPredictionContext(player?.Address ?? nint.Zero);
        if (player == null)
            return ClearFrame(out position, out yaw, out forward, out right);

        var control = Control.Instance();
        if (control == null)
            return ClearFrame(out position, out yaw, out forward, out right);

        var camera = control->CameraManager.GetActiveCamera();
        if (camera == null)
            return ClearFrame(out position, out yaw, out forward, out right);

        position = player.Position;
        if (!PlacementMath.IsFinite(position))
            return ClearFrame(out position, out yaw, out forward, out right);

        var renderCamera = camera->SceneCamera.RenderCamera;
        if (renderCamera != null)
        {
            var cameraOrigin = TryGetCurrentCameraOrigin(control->ViewProjectionMatrix, renderCamera->ProjectionMatrix, out var currentCameraOrigin)
                ? currentCameraOrigin
                : (Vector3)renderCamera->Origin;
            if (!PlacementMath.IsFinite(cameraOrigin))
                return ClearFrame(out position, out yaw, out forward, out right);

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

    private void ApplyFramePrediction(ScreenPlacementMode mode, ref Vector3 position, ref float yaw, float predictionSeconds)
    {
        if (mode is not (ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera))
            return;

        lock (predictionGate)
        {
            GetPredictionState(mode).Apply(
                ref position,
                ref yaw,
                predictionSeconds,
                Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }
    }

    private FollowFramePredictor GetPredictionState(ScreenPlacementMode mode)
    {
        if (mode == ScreenPlacementMode.FollowCamera)
            return cameraPrediction;

        return playerPrediction;
    }

    private void RefreshPredictionContext(nint playerAddress)
    {
        var territoryId = (ushort)clientState.TerritoryType;
        lock (predictionGate)
        {
            if (!predictionContext.Update(territoryId, playerAddress))
                return;

            playerPrediction.Reset();
            cameraPrediction.Reset();
        }
    }

    private static bool TryGetCurrentCameraOrigin(Matrix4x4 viewProjection, Matrix4x4 projection, out Vector3 origin)
    {
        if (!PlacementMath.IsFinite(viewProjection) || !PlacementMath.IsFinite(projection))
        {
            origin = default;
            return false;
        }

        if (!Matrix4x4.Invert(projection, out var inverseProjection))
        {
            origin = default;
            return false;
        }

        var view = viewProjection * inverseProjection;
        if (!Matrix4x4.Invert(view, out var inverseView))
        {
            origin = default;
            return false;
        }

        origin = new Vector3(inverseView.M41, inverseView.M42, inverseView.M43);
        return PlacementMath.IsFinite(origin);
    }

    private static bool TryGetYawPitchRoll(Quaternion rotation, out float yawRadians, out float pitchRadians, out float rollRadians)
    {
        if (!PlacementMath.IsFinite(rotation) || rotation.LengthSquared() <= 0.000001f)
        {
            yawRadians = 0.0f;
            pitchRadians = 0.0f;
            rollRadians = 0.0f;
            return false;
        }

        rotation = Quaternion.Normalize(rotation);
        yawRadians = MathF.Atan2(
            2.0f * ((rotation.W * rotation.Y) + (rotation.Z * rotation.X)),
            1.0f - (2.0f * ((rotation.X * rotation.X) + (rotation.Y * rotation.Y))));
        pitchRadians = MathF.Asin(Math.Clamp(
            2.0f * ((rotation.W * rotation.X) - (rotation.Y * rotation.Z)),
            -1.0f,
            1.0f));
        rollRadians = MathF.Atan2(
            2.0f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1.0f - (2.0f * ((rotation.X * rotation.X) + (rotation.Z * rotation.Z))));
        return float.IsFinite(yawRadians) && float.IsFinite(pitchRadians) && float.IsFinite(rollRadians);
    }

    private static bool ClearFrame(out Vector3 position, out float yaw, out Vector3 forward, out Vector3 right)
    {
        position = default;
        yaw = 0.0f;
        forward = default;
        right = default;
        return false;
    }
}
