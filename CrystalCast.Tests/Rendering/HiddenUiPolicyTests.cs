using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class HiddenUiPolicyTests
{
    [Fact]
    public void SceneCompositeKeepsScreensByDefault()
    {
        Assert.True(HiddenUiPolicy.KeepsScreens(new Configuration { OutputMode = ScreenOutputMode.SceneComposite }));
    }

    [Fact]
    public void TheSwitchTurnsItOff()
    {
        Assert.False(HiddenUiPolicy.KeepsScreens(new Configuration { OutputMode = ScreenOutputMode.SceneComposite, KeepScreensWhenUiHidden = false }));
    }

    [Fact]
    public void NativeOverlayIsLeftAlone()
    {
        Assert.False(HiddenUiPolicy.KeepsScreens(new Configuration { OutputMode = ScreenOutputMode.NativeOverlay }));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void WindowsHideOnlyWhenCrystalCastKeptDrawing(bool uiHidden, bool keepsScreens, bool expected)
    {
        Assert.Equal(expected, HiddenUiPolicy.ShowsWindows(uiHidden, keepsScreens));
    }

    [Fact]
    public void GposeHasItsOwnSwitch()
    {
        Assert.True(HiddenUiPolicy.KeepsScreensInGpose(new Configuration { OutputMode = ScreenOutputMode.SceneComposite }));
        Assert.False(HiddenUiPolicy.KeepsScreensInGpose(new Configuration { OutputMode = ScreenOutputMode.SceneComposite, KeepScreensInGpose = false }));
        Assert.False(HiddenUiPolicy.KeepsScreensInGpose(new Configuration { OutputMode = ScreenOutputMode.NativeOverlay }));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void WindowsHideInGposeOnlyWhenCrystalCastKeptDrawing(bool inGpose, bool keepsInGpose, bool expected)
    {
        Assert.Equal(expected, HiddenUiPolicy.ShowsWindows(false, false, inGpose, keepsInGpose));
    }
}
