namespace CrystalCast.Tests;

public sealed class ScreenLimitPolicyTests
{
    [Fact]
    public void UserScreensStopAtUserLimit()
    {
        var screens = CreateScreens(Configuration.MaxBrowserScreens, createdByIpc: false);

        Assert.False(ScreenLimitPolicy.CanCreateUserScreen(screens));
        Assert.True(ScreenLimitPolicy.CanCreateIpcScreen(screens));
    }

    [Fact]
    public void IpcScreensStopAtIpcLimit()
    {
        var screens = CreateScreens(Configuration.MaxIpcBrowserScreens, createdByIpc: true);

        Assert.False(ScreenLimitPolicy.CanCreateIpcScreen(screens));
        Assert.True(ScreenLimitPolicy.CanCreateUserScreen(screens));
    }

    [Fact]
    public void NeitherOwnerCanExceedTotalLimit()
    {
        var screens = CreateScreens(Configuration.MaxBrowserScreens, createdByIpc: false);
        screens.AddRange(CreateScreens(Configuration.MaxIpcBrowserScreens, createdByIpc: true));

        Assert.Equal(Configuration.MaxRenderableBrowserScreens, screens.Count);
        Assert.False(ScreenLimitPolicy.CanCreateUserScreen(screens));
        Assert.False(ScreenLimitPolicy.CanCreateIpcScreen(screens));
    }

    [Fact]
    public void CountsScreensByOwnership()
    {
        var screens = CreateScreens(3, createdByIpc: false);
        screens.AddRange(CreateScreens(5, createdByIpc: true));

        Assert.Equal(3, ScreenLimitPolicy.CountUserScreens(screens));
        Assert.Equal(5, ScreenLimitPolicy.CountIpcScreens(screens));
    }

    [Fact]
    public void AllowedScreensEnforceEachOwnershipQuotaRegardlessOfOrdering()
    {
        var screens = CreateScreens(Configuration.MaxIpcBrowserScreens + 1, createdByIpc: true);
        screens.InsertRange(1, CreateScreens(Configuration.MaxBrowserScreens + 1, createdByIpc: false));

        var allowed = ScreenLimitPolicy.GetAllowedScreens(screens);

        Assert.Equal(Configuration.MaxRenderableBrowserScreens, allowed.Count);
        Assert.Equal(Configuration.MaxBrowserScreens, ScreenLimitPolicy.CountUserScreens(allowed));
        Assert.Equal(Configuration.MaxIpcBrowserScreens, ScreenLimitPolicy.CountIpcScreens(allowed));
    }

    [Fact]
    public void ConfigurationDisablesProfilesOutsideOwnershipQuotas()
    {
        var configuration = new Configuration
        {
            BrowserScreens = CreateScreens(Configuration.MaxBrowserScreens + 1, createdByIpc: false),
        };
        configuration.BrowserScreens.AddRange(CreateScreens(Configuration.MaxIpcBrowserScreens + 1, createdByIpc: true));

        Assert.True(configuration.Normalize());

        Assert.Equal(Configuration.MaxRenderableBrowserScreens, configuration.BrowserScreens.Count(screen => screen.Enabled));
        Assert.Single(configuration.BrowserScreens, screen => !screen.CreatedByIpc && !screen.Enabled);
        Assert.Single(configuration.BrowserScreens, screen => screen.CreatedByIpc && !screen.Enabled);
    }

    [Fact]
    public void ActiveScreensEnforceRuntimeBudgetWithoutDisablingDeferredProfiles()
    {
        var screens = CreateScreens(Configuration.MaxBrowserScreens, createdByIpc: false);
        screens.AddRange(CreateScreens(2, createdByIpc: true));

        var active = ScreenLimitPolicy.GetActiveScreens(screens);

        Assert.Equal(Configuration.MaxActiveBrowserScreens, active.Count);
        Assert.Equal(2, ScreenLimitPolicy.CountDeferredActiveScreens(screens));
        Assert.All(screens, screen => Assert.True(screen.Enabled));
    }

    private static List<BrowserScreenProfile> CreateScreens(int count, bool createdByIpc)
    {
        return Enumerable.Range(0, count)
            .Select(_ => new BrowserScreenProfile { CreatedByIpc = createdByIpc })
            .ToList();
    }
}
