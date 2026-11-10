using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class RenderOutputPolicyTests
{
    [Fact]
    public void SceneCompositeIsTheDefaultEverywhere()
        => Assert.Equal(ScreenOutputMode.SceneComposite, RenderOutputPolicy.GetDefault());

    [Theory]
    [InlineData(true, false, "scene composite hook unavailable")]
    [InlineData(false, true, "scene composite timed out")]
    public void UnusableSceneOutputFallsBackAndStaysThere(bool unavailable, bool stalled, string reason)
    {
        var policy = new RenderOutputPolicy();
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, unavailable, stalled));
        Assert.Equal(reason, policy.FallbackReason);
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false));
    }

    [Fact]
    public void ChangingLayersOrReinitializingAllowsRetry()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.SceneComposite, false, true);
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.NativeOverlay, true, true));
        Assert.Null(policy.FallbackReason);
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false));

        policy.Resolve(ScreenOutputMode.SceneComposite, true, false);
        policy.ResetAfterGraphicsInitialization();
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false));
    }

    [Fact]
    public void HealthySceneOutputDoesNotFallBack()
    {
        var policy = new RenderOutputPolicy();
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false));
        Assert.Null(policy.FallbackReason);
    }

    [Fact]
    public void CallbackFailureDoesNotLoopAfterGraphicsRecovery()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.SceneComposite, false, false);
        policy.ReportSceneCompositeFailure();
        policy.ResetAfterGraphicsInitialization();
        Assert.Equal(ScreenOutputMode.NativeOverlay,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false));
        Assert.Equal("scene composite callback failed", policy.FallbackReason);

        // Switching layers and back retries.
        policy.Resolve(ScreenOutputMode.NativeOverlay, false, false);
        Assert.Equal(ScreenOutputMode.SceneComposite,
            policy.Resolve(ScreenOutputMode.SceneComposite, false, false));
        Assert.Null(policy.FallbackReason);
    }

    [Fact]
    public void OverlayFailureDoesNotDisableSceneOutput()
    {
        var policy = new RenderOutputPolicy();
        policy.Resolve(ScreenOutputMode.NativeOverlay, false, false);
        policy.ReportSceneCompositeFailure();
        Assert.Null(policy.FallbackReason);
    }
}
