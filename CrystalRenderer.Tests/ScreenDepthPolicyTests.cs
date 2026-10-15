using CrystalRenderer.D3D11;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace CrystalRenderer.Tests;

public sealed class ScreenDepthPolicyTests
{
    [Theory]
    [InlineData(Format.R24G8_Typeless, Format.R24_UNorm_X8_Typeless)]
    [InlineData(Format.R32_Typeless, Format.R32_Float)]
    [InlineData(Format.R16_Typeless, Format.R16_UNorm)]
    [InlineData(Format.R32G8X24_Typeless, Format.R32_Float_X8X24_Typeless)]
    public void SupportsDepthResourceFamilies(Format resource, Format expected)
    {
        Assert.True(ScreenDepthPolicy.TryGetShaderFormat(resource, 1, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(Format.R24G8_Typeless, 4)]
    [InlineData(Format.B8G8R8A8_UNorm, 1)]
    [InlineData(Format.D24_UNorm_S8_UInt, 1)]
    public void RejectsResourcesThatCannotBeSampledByTheSceneShader(Format resource, int samples)
        => Assert.False(ScreenDepthPolicy.TryGetShaderFormat(resource, samples, out _));

    [Theory]
    [InlineData(true, DepthWriteMask.All)]
    [InlineData(false, DepthWriteMask.Zero)]
    public void ScreensUseReverseDepthWithoutBreakingStencilClipping(bool opaque, DepthWriteMask writes)
    {
        var state = ScreenDepthPolicy.CreateImageState(opaque);
        Assert.True(state.IsDepthEnabled);
        Assert.Equal(Comparison.GreaterEqual, state.DepthComparison);
        Assert.Equal(writes, state.DepthWriteMask);
        Assert.True(state.IsStencilEnabled);
        Assert.Equal(0, state.StencilWriteMask);
        Assert.Equal(Comparison.Equal, state.FrontFace.Comparison);
        Assert.Equal(StencilOperation.Keep, state.FrontFace.PassOperation);
        Assert.Equal(state.FrontFace, state.BackFace);
    }
}
