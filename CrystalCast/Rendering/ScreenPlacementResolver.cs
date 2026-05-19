using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

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
    private const float MinPredictionSampleSeconds = 0.002f;
    private const float MaxPredictionSampleSeconds = 0.25f;
    private const float MaxPredictionLeadSeconds = 0.05f;
    private const float MaxPredictionStepMeters = 20.0f;
    private const float VelocityBlend = 0.65f;

    private static FramePredictionState playerPrediction;
    private static FramePredictionState cameraPrediction;

    public static bool TryResolve(ScreenPlacementSettings placement, out ResolvedScreenPlacement resolved)
    {
        return TryResolve(placement, 0.0f, out resolved);
    }

    public static bool TryResolve(ScreenPlacementSettings placement, float predictionFrames, out ResolvedScreenPlacement resolved)
    {
        if (placement.Mode != ScreenPlacementMode.World)
        {
            if (!TryGetPlacementFrame(placement.Mode, out var framePosition, out var frameYaw, out var forward, out var right))
            {
                resolved = default;
                return false;
            }

            ApplyFramePrediction(placement.Mode, ref framePosition, ref frameYaw, predictionFrames);
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

    public static bool TryApplyWorldPositionPreservingMode(ScreenPlacementSettings placement, Vector3 worldPosition)
    {
        if (!IsFinite(worldPosition))
            return false;

        if (placement.Mode == ScreenPlacementMode.World)
        {
            placement.PositionX = worldPosition.X;
            placement.PositionY = worldPosition.Y;
            placement.PositionZ = worldPosition.Z;
            placement.Normalize();
            return true;
        }

        if (!TryGetPlacementFrame(placement.Mode, out var framePosition, out _, out var forward, out var right))
            return false;

        var offset = worldPosition - framePosition;
        placement.PositionX = Vector3.Dot(offset, right);
        placement.PositionY = offset.Y;
        placement.PositionZ = Vector3.Dot(offset, forward);
        placement.Normalize();
        return true;
    }

    public static bool TryApplyWorldRotationPreservingMode(ScreenPlacementSettings placement, Quaternion worldRotation)
    {
        if (!TryGetYawPitchRoll(worldRotation, out var yawRadians, out var pitchRadians, out var rollRadians))
            return false;

        if (placement.Mode != ScreenPlacementMode.World)
        {
            if (!TryGetPlacementFrame(placement.Mode, out _, out var frameYaw, out _, out _))
                return false;

            yawRadians = NormalizeRadians(yawRadians - frameYaw);
        }

        placement.YawRadians = NormalizeRadians(yawRadians);
        placement.PitchRadians = NormalizeRadians(pitchRadians);
        placement.RollRadians = NormalizeRadians(rollRadians);
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
        if (TryGetRenderedObjectPosition(player.Address, out var renderedPosition))
            position = renderedPosition;

        yaw = TryGetRenderedObjectYaw(player.Address, out var renderedYaw)
            ? renderedYaw
            : player.Rotation;
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
        if (TryGetRenderedObjectPosition(player.Address, out var renderedPosition))
            position = renderedPosition;

        var renderCamera = camera->SceneCamera.RenderCamera;
        if (renderCamera != null)
        {
            var cameraOrigin = TryGetCurrentCameraOrigin(Control.Instance()->ViewProjectionMatrix, renderCamera->ProjectionMatrix, out var currentCameraOrigin)
                ? currentCameraOrigin
                : (Vector3)renderCamera->Origin;
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

    private static void ApplyFramePrediction(ScreenPlacementMode mode, ref Vector3 position, ref float yaw, float predictionFrames)
    {
        if (mode is not (ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera))
            return;

        ref var state = ref GetPredictionState(mode);
        var now = Stopwatch.GetTimestamp();
        if (!state.HasSample)
        {
            state = FramePredictionState.Create(now, position, yaw);
            return;
        }

        var elapsedSeconds = (float)((now - state.LastTimestamp) / (double)Stopwatch.Frequency);
        if (elapsedSeconds >= MinPredictionSampleSeconds)
        {
            var step = position - state.LastPosition;
            if (elapsedSeconds > MaxPredictionSampleSeconds
                || !IsFinite(position)
                || !float.IsFinite(yaw)
                || step.LengthSquared() > MaxPredictionStepMeters * MaxPredictionStepMeters)
            {
                state = FramePredictionState.Create(now, position, yaw);
                return;
            }

            var sampleVelocity = step / elapsedSeconds;
            var sampleYawVelocity = NormalizeRadians(yaw - state.LastYaw) / elapsedSeconds;
            state.Velocity = Vector3.Lerp(state.Velocity, sampleVelocity, VelocityBlend);
            state.YawVelocity += (sampleYawVelocity - state.YawVelocity) * VelocityBlend;
            state.LastDeltaSeconds = elapsedSeconds;
            state.LastTimestamp = now;
            state.LastPosition = position;
            state.LastYaw = yaw;
        }

        var predictionSeconds = Math.Clamp(predictionFrames, 0.0f, 3.0f) * state.LastDeltaSeconds;
        predictionSeconds = Math.Clamp(predictionSeconds, 0.0f, MaxPredictionLeadSeconds);
        position += state.Velocity * predictionSeconds;
        yaw = NormalizeRadians(yaw + (state.YawVelocity * predictionSeconds));
    }

    private static ref FramePredictionState GetPredictionState(ScreenPlacementMode mode)
    {
        if (mode == ScreenPlacementMode.FollowCamera)
            return ref cameraPrediction;

        return ref playerPrediction;
    }

    private static unsafe bool TryGetRenderedObjectPosition(nint objectAddress, out Vector3 position)
    {
        if (objectAddress == nint.Zero)
        {
            position = default;
            return false;
        }

        var gameObject = (GameObjectStruct*)objectAddress;
        var drawObject = gameObject->DrawObject;
        if (drawObject == null)
        {
            position = default;
            return false;
        }

        position = (Vector3)drawObject->Position;
        return IsFinite(position);
    }

    private static unsafe bool TryGetRenderedObjectYaw(nint objectAddress, out float yaw)
    {
        if (objectAddress == nint.Zero)
        {
            yaw = 0.0f;
            return false;
        }

        var gameObject = (GameObjectStruct*)objectAddress;
        var drawObject = gameObject->DrawObject;
        if (drawObject == null)
        {
            yaw = 0.0f;
            return false;
        }

        var rotation = Quaternion.Normalize((Quaternion)drawObject->Rotation);
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        forward.Y = 0.0f;
        if (forward.LengthSquared() <= 0.0001f)
        {
            yaw = 0.0f;
            return false;
        }

        forward = Vector3.Normalize(forward);
        yaw = MathF.Atan2(forward.X, forward.Z);
        return float.IsFinite(yaw);
    }

    private static bool TryGetCurrentCameraOrigin(Matrix4x4 viewProjection, Matrix4x4 projection, out Vector3 origin)
    {
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
        return IsFinite(origin);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W);
    }

    private static bool TryGetYawPitchRoll(Quaternion rotation, out float yawRadians, out float pitchRadians, out float rollRadians)
    {
        if (!IsFinite(rotation) || rotation.LengthSquared() <= 0.000001f)
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

    private static float NormalizeRadians(float value)
    {
        while (value <= -MathF.PI)
            value += MathF.Tau;
        while (value > MathF.PI)
            value -= MathF.Tau;
        return value;
    }

    private struct FramePredictionState
    {
        public bool HasSample;
        public long LastTimestamp;
        public float LastDeltaSeconds;
        public Vector3 LastPosition;
        public float LastYaw;
        public Vector3 Velocity;
        public float YawVelocity;

        public static FramePredictionState Create(long timestamp, Vector3 position, float yaw)
        {
            return new FramePredictionState
            {
                HasSample = true,
                LastTimestamp = timestamp,
                LastDeltaSeconds = 1.0f / 60.0f,
                LastPosition = position,
                LastYaw = yaw,
            };
        }
    }
}
