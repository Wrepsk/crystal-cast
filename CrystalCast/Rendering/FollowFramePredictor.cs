using System.Numerics;

namespace CrystalCast.Rendering;

internal sealed class FollowFramePredictor
{
    private const double MinSampleSeconds = 0.002;
    private const double MaxSampleSeconds = 0.25;
    private const double IdleHoldSeconds = 0.05;
    private const float VelocitySmoothingSeconds = 0.06f;
    private const float IdleDampingSeconds = 0.06f;
    private const float PositionSmoothingSeconds = 0.05f;
    private const float YawSmoothingSeconds = 0.065f;
    private const float PositionChangeEpsilonMeters = 0.001f;
    private const float YawChangeEpsilonRadians = 0.0002f;
    private const float MaxSampleStepMeters = 20.0f;
    private const float MaxPredictionSeconds = 0.05f;
    private const float MaxPositionLeadMeters = 1.5f;
    private const float MaxYawLeadRadians = 0.5f;

    private bool initialized;
    private double lastEvaluationSeconds;
    private double lastPositionSampleSeconds;
    private double lastYawSampleSeconds;
    private Vector3 lastPosition;
    private float lastYaw;
    private Vector3 velocity;
    private float yawVelocity;
    private Vector3 filteredPosition;
    private float filteredYaw;

    public void Apply(ref Vector3 position, ref float yaw, float predictionSeconds, double nowSeconds)
    {
        if (!IsFinite(position) || !float.IsFinite(yaw) || !double.IsFinite(nowSeconds))
        {
            Reset();
            return;
        }

        if (!initialized || nowSeconds < lastEvaluationSeconds || nowSeconds - lastEvaluationSeconds > MaxSampleSeconds)
        {
            Initialize(position, yaw, nowSeconds);
            return;
        }

        var evaluationDelta = nowSeconds - lastEvaluationSeconds;
        if (evaluationDelta < MinSampleSeconds)
        {
            position = filteredPosition;
            yaw = filteredYaw;
            return;
        }

        lastEvaluationSeconds = nowSeconds;

        UpdatePosition(position, nowSeconds, evaluationDelta);
        UpdateYaw(yaw, nowSeconds, evaluationDelta);

        var outputLatencySeconds = float.IsFinite(predictionSeconds)
            ? Math.Clamp(predictionSeconds, 0.0f, MaxPredictionSeconds)
            : 0.0f;
        var positionLead = velocity * (outputLatencySeconds + PositionSmoothingSeconds);
        var positionLeadLength = positionLead.Length();
        if (positionLeadLength > MaxPositionLeadMeters)
            positionLead *= MaxPositionLeadMeters / positionLeadLength;

        var positionTarget = position + positionLead;
        var positionBlend = GetFilterBlend(evaluationDelta, PositionSmoothingSeconds);
        filteredPosition = Vector3.Lerp(filteredPosition, positionTarget, positionBlend);

        var yawLead = Math.Clamp(
            yawVelocity * (outputLatencySeconds + YawSmoothingSeconds),
            -MaxYawLeadRadians,
            MaxYawLeadRadians);
        var yawTarget = NormalizeRadians(yaw + yawLead);
        var yawBlend = GetFilterBlend(evaluationDelta, YawSmoothingSeconds);
        filteredYaw = NormalizeRadians(filteredYaw + (NormalizeRadians(yawTarget - filteredYaw) * yawBlend));

        position = filteredPosition;
        yaw = filteredYaw;
    }

    public void Reset()
    {
        initialized = false;
        lastEvaluationSeconds = 0.0;
        lastPositionSampleSeconds = 0.0;
        lastYawSampleSeconds = 0.0;
        lastPosition = default;
        lastYaw = 0.0f;
        velocity = default;
        yawVelocity = 0.0f;
        filteredPosition = default;
        filteredYaw = 0.0f;
    }

    private void UpdatePosition(Vector3 position, double nowSeconds, double evaluationDelta)
    {
        var step = position - lastPosition;
        if (step.LengthSquared() >= PositionChangeEpsilonMeters * PositionChangeEpsilonMeters)
        {
            var sampleDelta = nowSeconds - lastPositionSampleSeconds;
            if (sampleDelta >= MinSampleSeconds)
            {
                if (sampleDelta > MaxSampleSeconds || step.LengthSquared() > MaxSampleStepMeters * MaxSampleStepMeters)
                {
                    velocity = default;
                }
                else
                {
                    var sampleVelocity = step / (float)sampleDelta;
                    velocity = Vector3.Lerp(velocity, sampleVelocity, GetSmoothingBlend(sampleDelta));
                }

                lastPosition = position;
                lastPositionSampleSeconds = nowSeconds;
            }

            return;
        }

        if (nowSeconds - lastPositionSampleSeconds > IdleHoldSeconds)
            velocity *= GetIdleDecay(evaluationDelta);
    }

    private void UpdateYaw(float yaw, double nowSeconds, double evaluationDelta)
    {
        var yawStep = NormalizeRadians(yaw - lastYaw);
        if (MathF.Abs(yawStep) >= YawChangeEpsilonRadians)
        {
            var sampleDelta = nowSeconds - lastYawSampleSeconds;
            if (sampleDelta >= MinSampleSeconds)
            {
                if (sampleDelta > MaxSampleSeconds)
                {
                    yawVelocity = 0.0f;
                }
                else
                {
                    var sampleYawVelocity = yawStep / (float)sampleDelta;
                    yawVelocity += (sampleYawVelocity - yawVelocity) * GetSmoothingBlend(sampleDelta);
                }

                lastYaw = yaw;
                lastYawSampleSeconds = nowSeconds;
            }

            return;
        }

        if (nowSeconds - lastYawSampleSeconds > IdleHoldSeconds)
            yawVelocity *= GetIdleDecay(evaluationDelta);
    }

    private void Initialize(Vector3 position, float yaw, double nowSeconds)
    {
        initialized = true;
        lastEvaluationSeconds = nowSeconds;
        lastPositionSampleSeconds = nowSeconds;
        lastYawSampleSeconds = nowSeconds;
        lastPosition = position;
        lastYaw = yaw;
        velocity = default;
        yawVelocity = 0.0f;
        filteredPosition = position;
        filteredYaw = yaw;
    }

    private static float GetSmoothingBlend(double sampleSeconds)
    {
        return 1.0f - MathF.Exp(-(float)sampleSeconds / VelocitySmoothingSeconds);
    }

    private static float GetIdleDecay(double evaluationSeconds)
    {
        if (evaluationSeconds <= 0.0)
            return 1.0f;

        return MathF.Exp(-(float)evaluationSeconds / IdleDampingSeconds);
    }

    private static float GetFilterBlend(double evaluationSeconds, float smoothingSeconds)
    {
        return 1.0f - MathF.Exp(-(float)evaluationSeconds / smoothingSeconds);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z);
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
