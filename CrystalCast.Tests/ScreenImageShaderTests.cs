using SharpDX.D3DCompiler;
using ScreenImage = Pictomancy.DXDraw.Image;

namespace CrystalCast.Tests;

public sealed class ScreenImageShaderTests
{
    [Theory]
    [InlineData("vs", "vs_5_0")]
    [InlineData("psOpaque", "ps_5_0")]
    [InlineData("ps", "ps_5_0")]
    public void ScreenPassesCompileWithoutAGraphicsDevice(string entry, string profile)
    {
        using var result = ShaderBytecode.Compile(ScreenImage.FlatShaderSource, entry, profile);
        Assert.False(result.HasErrors, result.Message);
        Assert.NotNull(result.Bytecode);
    }
}
