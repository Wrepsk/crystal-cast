using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class ScreenStateValidatorTests
{
    [Fact]
    public void AcceptsBoundedBrowserState()
    {
        const long now = 1_000_000;
        var state = RemoteScreenStateStoreTests.CreateState("owner", "screen", 1, now);

        Assert.True(ScreenStateValidator.TryValidate(state, now, out var error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void RejectsMissingOrUnknownProvider(string provider)
    {
        const long now = 1_000_000;
        var state = RemoteScreenStateStoreTests.CreateState("owner", "screen", 1, now);
        state.Source.Provider = provider;

        Assert.False(ScreenStateValidator.TryValidate(state, now, out _));
    }

    [Fact]
    public void RejectsNonFinitePlacement()
    {
        const long now = 1_000_000;
        var state = RemoteScreenStateStoreTests.CreateState("owner", "screen", 1, now);
        state.Position = new Vector3Dto(float.NaN, 0, 0);

        Assert.False(ScreenStateValidator.TryValidate(state, now, out _));
    }

    [Fact]
    public void RejectsExcessivelyFutureTimestamp()
    {
        const long now = 1_000_000;
        var state = RemoteScreenStateStoreTests.CreateState("owner", "screen", 1, now);
        state.TimestampUnixMs = now + (6 * 60 * 1000);

        Assert.False(ScreenStateValidator.TryValidate(state, now, out _));
    }
}
