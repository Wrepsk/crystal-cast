namespace CrystalCast.Rendering;

internal static class CurvedScreenTessellation
{
    public const int MaxSegments = 32;
    private const int MinSegments = 4;
    private const float TargetRadiansPerSegment = MathF.PI / MaxSegments;

    public static int GetSegmentCount(float halfAngleRadians)
    {
        if (!float.IsFinite(halfAngleRadians) || halfAngleRadians <= 0.0f)
            return MinSegments;

        var fullAngle = MathF.Abs(halfAngleRadians) * 2.0f;
        return Math.Clamp((int)MathF.Ceiling(fullAngle / TargetRadiansPerSegment), MinSegments, MaxSegments);
    }
}
