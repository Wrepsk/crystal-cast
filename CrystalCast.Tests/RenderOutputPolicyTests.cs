using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class RenderOutputPolicyTests
{
    [Theory]
    [InlineData(true, false, ScreenOutputMode.SceneComposite)]
    [InlineData(true, true, ScreenOutputMode.NativeOverlay)]
    [InlineData(false, false, ScreenOutputMode.NativeOverlay)]
    public void DefaultAccountsForWine(bool windows, bool wine, ScreenOutputMode expected)
        => Assert.Equal(expected, RenderOutputPolicy.GetDefault(windows, wine));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void UnusableSceneOutputFallsBackAndStaysThere(bool wine, bool unavailable, bool stalled)
    {
        var policy = new RenderOutputPolicy();
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, wine, unavailable, stalled));
        Assert.NotNull(policy.FallbackReason);
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false, false));
    }

    [Fact]
    public void ChangingLayersOrReinitializingAllowsRetry()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.SceneComposite, false, false, true);
        Assert.Equal(ScreenOutputMode.ImGuiOverlay,
            policy.Resolve(ScreenOutputMode.ImGuiOverlay, false, true, true));
        Assert.Null(policy.FallbackReason);
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false, false));
        policy.Resolve(ScreenOutputMode.SceneComposite, false, true, false);
        policy.Reset();
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false, false));
    }

    [Fact]
    public void HealthySceneOutputDoesNotFallBack()
    {
        var policy = new RenderOutputPolicy();
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false, false));
        Assert.Null(policy.FallbackReason);
    }
}
