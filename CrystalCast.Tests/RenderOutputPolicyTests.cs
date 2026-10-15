using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class RenderOutputPolicyTests
{
    [Theory]
    [InlineData(true, false, ScreenOutputMode.SceneComposite)]
    [InlineData(true, true, ScreenOutputMode.SceneComposite)]
    [InlineData(false, true, ScreenOutputMode.SceneComposite)]
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

    [Fact]
    public void WineCanExplicitlyTryHealthySceneOutput()
    {
        var policy = new RenderOutputPolicy();
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false));
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, enableWineSceneComposite: true));
        Assert.Null(policy.FallbackReason);
    }

    [Theory]
    [InlineData(true, false, "scene composite hook unavailable")]
    [InlineData(false, true, "scene composite timed out")]
    public void WineOptInRetainsHookAndTimeoutFallback(bool unavailable, bool stalled, string reason)
    {
        var policy = new RenderOutputPolicy();
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, unavailable, stalled, true));
        Assert.Equal(reason, policy.FallbackReason);
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true));
        policy.ResetAfterGraphicsInitialization();
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true));
    }

    [Fact]
    public void DisablingWineOptInStopsSceneOutputAndReenablingAllowsRetry()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true);
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, false));
        Assert.Equal("experimental Wine scene compositing is not enabled", policy.FallbackReason);
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true));
    }

    [Fact]
    public void CallbackFailureDoesNotLoopAfterGraphicsRecovery()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true);
        policy.ReportSceneCompositeFailure();
        policy.ResetAfterGraphicsInitialization();
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true));
        Assert.Equal("scene composite callback failed", policy.FallbackReason);

        policy.Resolve(ScreenOutputMode.NativeOverlay, true, false, false, true);
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true));
        Assert.Null(policy.FallbackReason);
    }

    [Fact]
    public void CallbackFailureCanBeRetriedByTogglingWineOptIn()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true);
        policy.ReportSceneCompositeFailure();
        policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, false);
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, true, false, false, true));
    }

    [Fact]
    public void OverlayFailureDoesNotDisableSceneOutput()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.NativeOverlay, true, false, false, true);
        policy.ReportSceneCompositeFailure();
        Assert.Null(policy.FallbackReason);
    }

    [Fact]
    public void WineOptionHasNoEffectOnWindowsOutputOrItsLatchedFallback()
    {
        var policy = new RenderOutputPolicy();
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false, false, true));
        policy.Resolve(ScreenOutputMode.SceneComposite, false, false, true, true);
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false, false, false));
    }
}
