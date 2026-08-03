using CrystalCast.Windows;

namespace CrystalCast.Tests;

public sealed class FirstRunGuidePolicyTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    public void ShowsOnlyForEnabledFirstRunAfterPrerequisiteCloses(
        bool completed,
        bool pluginEnabled,
        bool prerequisiteWindowOpen,
        bool expected)
    {
        Assert.Equal(expected, FirstRunGuidePolicy.ShouldShow(completed, pluginEnabled, prerequisiteWindowOpen));
    }
}
