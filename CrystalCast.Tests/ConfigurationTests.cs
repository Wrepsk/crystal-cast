namespace CrystalCast.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void NewConfigurationNormalizesToBrowserMode()
    {
        var configuration = new Configuration();

        var changed = configuration.Normalize();

        Assert.True(changed);
        Assert.Equal(2, configuration.Version);
        Assert.Equal(ScreenSourceKind.Browser, configuration.SourceKind);
        Assert.Single(configuration.BrowserScreens);
    }

    [Fact]
    public void LegacyLocalVideoConfigurationPreservesPlacementAndDisablesMigratedScreen()
    {
        var configuration = new Configuration
        {
            Version = 1,
            SourceKind = (ScreenSourceKind)2,
            BrowserScreens = [],
            LocalVideoPlacementMode = ScreenPlacementMode.FollowPlayer,
            PositionX = 1.25f,
            PositionY = 2.5f,
            PositionZ = 4.75f,
            WidthMeters = 5.5f,
            ScreenCurveAmountMeters = 0.4f,
        };

        Assert.True(configuration.Normalize());

        var screen = Assert.Single(configuration.BrowserScreens);
        Assert.Equal(2, configuration.Version);
        Assert.Equal(ScreenSourceKind.Browser, configuration.SourceKind);
        Assert.False(screen.Enabled);
        Assert.Equal(ScreenPlacementMode.FollowPlayer, screen.Placement.Mode);
        Assert.Equal(1.25f, screen.Placement.PositionX);
        Assert.Equal(2.5f, screen.Placement.PositionY);
        Assert.Equal(4.75f, screen.Placement.PositionZ);
        Assert.Equal(5.5f, screen.Placement.WidthMeters);
        Assert.Equal(0.4f, screen.Placement.ScreenCurveAmountMeters);
    }

    [Fact]
    public void LegacyBrowserConfigurationKeepsExistingScreenEnabled()
    {
        var screen = new BrowserScreenProfile { Enabled = true, YouTubeUrl = "dQw4w9WgXcQ" };
        var configuration = new Configuration
        {
            Version = 1,
            SourceKind = ScreenSourceKind.Browser,
            BrowserScreens = [screen],
            ActiveBrowserScreenId = screen.ScreenId,
        };

        configuration.Normalize();

        Assert.True(screen.Enabled);
        Assert.Equal("dQw4w9WgXcQ", screen.YouTubeUrl);
        Assert.Equal(2, configuration.Version);
    }

    [Fact]
    public void BrowserAudioDefaultsToMutedForEveryProvider()
    {
        var screen = new BrowserScreenProfile();

        Assert.False(screen.YouTubeAudioEnabled);
        Assert.False(screen.TwitchAudioEnabled);
        Assert.False(screen.DailymotionAudioEnabled);
        Assert.False(screen.VimeoAudioEnabled);
        Assert.False(screen.GenericWebAudioEnabled);
    }

    [Fact]
    public void NormalizeRepairsDuplicateScreenIds()
    {
        const string duplicateId = "duplicate";
        var first = new BrowserScreenProfile { ScreenId = duplicateId };
        var second = new BrowserScreenProfile { ScreenId = duplicateId };
        var configuration = new Configuration
        {
            BrowserScreens = [first, second],
            ActiveBrowserScreenId = duplicateId,
        };

        configuration.Normalize();

        Assert.Equal(2, configuration.BrowserScreens.Select(screen => screen.ScreenId).Distinct().Count());
        Assert.Equal(duplicateId, first.ScreenId);
        Assert.NotEqual(duplicateId, second.ScreenId);
    }
}
