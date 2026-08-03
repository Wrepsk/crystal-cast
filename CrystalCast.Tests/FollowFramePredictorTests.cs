using System.Numerics;
using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class FollowFramePredictorTests
{
    [Fact]
    public void DuplicatePositionSamplesContinueSmoothMovement()
    {
        var predictor = new FollowFramePredictor();
        var initialPosition = PredictX(predictor, 0.0f, 0.000);
        var firstFilteredPosition = PredictX(predictor, 0.2f, 0.016);
        var duplicateFilteredPosition = PredictX(predictor, 0.2f, 0.024);

        Assert.Equal(0.0f, initialPosition);
        Assert.InRange(firstFilteredPosition, 0.0f, 0.2f);
        Assert.InRange(duplicateFilteredPosition, firstFilteredPosition, 0.2f);
    }

    [Fact]
    public void DuplicateYawSamplesContinueSmoothRotation()
    {
        var predictor = new FollowFramePredictor();
        PredictYaw(predictor, 0.0f, 0.000);
        var firstFilteredYaw = PredictYaw(predictor, 0.2f, 0.016);
        var duplicateFilteredYaw = PredictYaw(predictor, 0.2f, 0.024);

        Assert.InRange(firstFilteredYaw, 0.0f, 0.2f);
        Assert.InRange(duplicateFilteredYaw, firstFilteredYaw, 0.2f);
    }

    [Fact]
    public void PredictionSettlesAfterMovementStops()
    {
        var predictor = new FollowFramePredictor();
        PredictX(predictor, 0.0f, 0.000);
        var peak = PredictX(predictor, 0.2f, 0.016);
        var settled = peak;
        for (var frame = 2; frame <= 40; frame++)
        {
            settled = PredictX(predictor, 0.2f, frame * 0.016);
            peak = Math.Max(peak, settled);
        }

        Assert.InRange(peak, 0.0f, 0.4f);
        Assert.InRange(settled, 0.195f, 0.205f);
    }

    [Fact]
    public void ExtremeVelocitySampleDoesNotSnapOrOvershoot()
    {
        var predictor = new FollowFramePredictor();
        PredictX(predictor, 0.0f, 0.000);
        var predicted = PredictX(predictor, 10.0f, 0.010, predictionSeconds: 0.05f);

        Assert.InRange(predicted, 0.0f, 10.0f);
    }

    [Fact]
    public void ResetDropsPreviousMovementVelocity()
    {
        var predictor = new FollowFramePredictor();
        PredictX(predictor, 0.0f, 0.000);
        Assert.True(PredictX(predictor, 0.2f, 0.016) > 0.0f);

        predictor.Reset();

        Assert.Equal(5.0f, PredictX(predictor, 5.0f, 0.032));
    }

    [Fact]
    public void AlternatingPositionNoiseIsAttenuated()
    {
        var predictor = new FollowFramePredictor();
        PredictX(predictor, 0.0f, 0.000, predictionSeconds: 0.0f);
        var filteredMinimum = float.PositiveInfinity;
        var filteredMaximum = float.NegativeInfinity;

        for (var frame = 1; frame <= 80; frame++)
        {
            var rawPosition = frame % 2 == 0 ? 0.05f : -0.05f;
            var filtered = PredictX(predictor, rawPosition, frame * 0.016, predictionSeconds: 0.0f);
            if (frame <= 40)
                continue;

            filteredMinimum = Math.Min(filteredMinimum, filtered);
            filteredMaximum = Math.Max(filteredMaximum, filtered);
        }

        Assert.True(filteredMaximum - filteredMinimum < 0.05f);
    }

    [Fact]
    public void VelocityCompensationAvoidsSteadyMovementLag()
    {
        const float velocityMetersPerSecond = 10.0f;
        const float frameSeconds = 0.016f;
        var predictor = new FollowFramePredictor();
        var filtered = PredictX(predictor, 0.0f, 0.000, predictionSeconds: 0.0f);

        for (var frame = 1; frame <= 120; frame++)
        {
            var time = frame * frameSeconds;
            filtered = PredictX(
                predictor,
                velocityMetersPerSecond * time,
                time,
                predictionSeconds: 0.0f);
        }

        var rawPosition = velocityMetersPerSecond * 120 * frameSeconds;
        Assert.InRange(filtered, rawPosition - 0.1f, rawPosition + 0.1f);
    }

    private static float PredictX(
        FollowFramePredictor predictor,
        float rawPositionX,
        double nowSeconds,
        float predictionSeconds = 1.0f / 60.0f)
    {
        var position = new Vector3(rawPositionX, 0.0f, 0.0f);
        var yaw = 0.0f;
        predictor.Apply(ref position, ref yaw, predictionSeconds, nowSeconds);
        return position.X;
    }

    private static float PredictYaw(
        FollowFramePredictor predictor,
        float rawYaw,
        double nowSeconds,
        float predictionSeconds = 1.0f / 60.0f)
    {
        var position = Vector3.Zero;
        predictor.Apply(ref position, ref rawYaw, predictionSeconds, nowSeconds);
        return rawYaw;
    }
}
