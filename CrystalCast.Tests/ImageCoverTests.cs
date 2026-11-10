using System.Numerics;
using CrystalCast.Windows;

namespace CrystalCast.Tests;

public sealed class ImageCoverTests
{
    [Fact]
    public void A4By3ImageIsCroppedTopAndBottomFor16By9()
    {
        var (uv0, uv1) = CrystalCastUiWidgets.CoverUv(4.0f / 3.0f, 16.0f / 9.0f);
        Assert.Equal(0.0f, uv0.X);
        Assert.Equal(1.0f, uv1.X);
        Assert.Equal(0.125f, uv0.Y, 3);
        Assert.Equal(0.875f, uv1.Y, 3);
    }

    [Fact]
    public void AWideImageIsCroppedAtTheSides()
    {
        var (uv0, uv1) = CrystalCastUiWidgets.CoverUv(2.0f, 1.0f);
        Assert.Equal(0.25f, uv0.X, 3);
        Assert.Equal(0.75f, uv1.X, 3);
        Assert.Equal(0.0f, uv0.Y);
        Assert.Equal(1.0f, uv1.Y);
    }

    [Fact]
    public void AMatchingImageIsNotCropped()
    {
        Assert.Equal((Vector2.Zero, Vector2.One), CrystalCastUiWidgets.CoverUv(16.0f / 9.0f, 16.0f / 9.0f));
        Assert.Equal((Vector2.Zero, Vector2.One), CrystalCastUiWidgets.CoverUv(0.0f, 1.0f));
    }
}
