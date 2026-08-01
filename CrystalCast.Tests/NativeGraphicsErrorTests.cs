using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class NativeGraphicsErrorTests
{
    [Theory]
    [InlineData(unchecked((int)0x887A0005))]
    [InlineData(unchecked((int)0x887A0006))]
    [InlineData(unchecked((int)0x887A0007))]
    [InlineData(unchecked((int)0x887A0020))]
    public void RecognizesDeviceLossHResults(int hresult)
    {
        var exception = NativeGraphicsError.FromHResult(hresult);

        Assert.True(NativeGraphicsError.IsDeviceLost(exception));
        Assert.False(NativeGraphicsError.IsWaitTimeout(exception));
    }

    [Fact]
    public void RecognizesKeyedMutexTimeout()
    {
        var exception = NativeGraphicsError.FromHResult(unchecked((int)0x887A0027));

        Assert.True(NativeGraphicsError.IsWaitTimeout(exception));
        Assert.False(NativeGraphicsError.IsDeviceLost(exception));
    }

    [Fact]
    public void SearchesInnerExceptionChain()
    {
        var inner = NativeGraphicsError.FromHResult(unchecked((int)0x887A0007));

        Assert.True(NativeGraphicsError.IsDeviceLost(new InvalidOperationException("wrapper", inner)));
    }
}
