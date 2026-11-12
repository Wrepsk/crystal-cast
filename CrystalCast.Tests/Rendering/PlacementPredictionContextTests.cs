using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class PlacementPredictionContextTests
{
    [Fact]
    public void FirstObservationRequiresPredictionReset()
    {
        var context = new PlacementPredictionContext();

        Assert.True(context.Update(100, (nint)0x1000));
        Assert.False(context.Update(100, (nint)0x1000));
    }

    [Fact]
    public void TerritoryChangeRequiresPredictionReset()
    {
        var context = new PlacementPredictionContext();
        context.Update(100, (nint)0x1000);

        Assert.True(context.Update(101, (nint)0x1000));
        Assert.False(context.Update(101, (nint)0x1000));
    }

    [Fact]
    public void PlayerChangeOrLogoutRequiresPredictionReset()
    {
        var context = new PlacementPredictionContext();
        context.Update(100, (nint)0x1000);

        Assert.True(context.Update(100, (nint)0x2000));
        Assert.True(context.Update(100, nint.Zero));
        Assert.False(context.Update(100, nint.Zero));
    }
}
