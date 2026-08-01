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

    private static List<BrowserScreenProfile> CreateScreens(int count, bool createdByIpc)
    {
        return Enumerable.Range(0, count)
            .Select(_ => new BrowserScreenProfile { CreatedByIpc = createdByIpc })
            .ToList();
    }
}
