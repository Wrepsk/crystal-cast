using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class CurvedScreenTessellationTests
{
    [Fact]
    public void SegmentCountGrowsWithCurveAngle()
    {
        var shallow = CurvedScreenTessellation.GetSegmentCount(0.1f);
        var medium = CurvedScreenTessellation.GetSegmentCount(0.5f);
        var deep = CurvedScreenTessellation.GetSegmentCount(MathF.PI * 0.5f);

        Assert.InRange(shallow, 4, medium - 1);
        Assert.InRange(medium, shallow + 1, deep - 1);
        Assert.Equal(CurvedScreenTessellation.MaxSegments, deep);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    public void InvalidOrFlatAnglesUseMinimumSegments(float halfAngle)
    {
        Assert.Equal(4, CurvedScreenTessellation.GetSegmentCount(halfAngle));
    }
}
