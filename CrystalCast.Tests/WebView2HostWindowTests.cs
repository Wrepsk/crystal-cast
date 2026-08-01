using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class WebView2HostWindowTests
{
    private const uint WmActivate = 0x0006;
    private const uint WmKillFocus = 0x0008;
    private const uint WmClose = 0x0010;

    [Fact]
    public void ActivationMessagesDoNotControlInteractionVisibility()
    {
        Assert.False(WebView2HostWindow.IsInteractionDismissMessage(WmActivate));
    }

    [Fact]
    public void ChildFocusMessagesDoNotDismissTheInteractionWindow()
    {
        Assert.False(WebView2HostWindow.IsInteractionDismissMessage(WmKillFocus));
    }

    [Fact]
    public void CloseButtonDismissesInsteadOfDestroyingTheCaptureWindow()
    {
        Assert.True(WebView2HostWindow.IsInteractionDismissMessage(WmClose));
    }

    [Fact]
    public void ForegroundInteractionWindowRemainsVisible()
    {
        Assert.False(WebView2HostWindow.ShouldDismissInteraction(
            isForeground: true,
            wasForeground: true,
            elapsedMilliseconds: 5_000));
    }

    [Fact]
    public void PreviouslyFocusedInteractionWindowDismissesImmediatelyAfterFocusMovesAway()
    {
        Assert.True(WebView2HostWindow.ShouldDismissInteraction(
            isForeground: false,
            wasForeground: true,
            elapsedMilliseconds: 1));
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    public void InteractionWindowGetsGracePeriodToBecomeForeground(long elapsedMilliseconds, bool expected)
    {
        Assert.Equal(expected, WebView2HostWindow.ShouldDismissInteraction(
            isForeground: false,
            wasForeground: false,
            elapsedMilliseconds));
    }

    [Theory]
    [InlineData(0, 100, true)]
    [InlineData(100, 599, false)]
    [InlineData(100, 600, true)]
    public void AutomaticDismissalTemporarilySuppressesReopening(
        long dismissedAtTick,
        long currentTick,
        bool expected)
    {
        Assert.Equal(expected, WebView2HostWindow.CanReopenInteraction(dismissedAtTick, currentTick));
    }
}
