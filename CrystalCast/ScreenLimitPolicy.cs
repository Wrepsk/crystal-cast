namespace CrystalCast;

internal static class ScreenLimitPolicy
{
    public static int CountUserScreens(IEnumerable<BrowserScreenProfile> screens)
    {
        return screens.Count(screen => !screen.CreatedByIpc);
    }

    public static int CountIpcScreens(IEnumerable<BrowserScreenProfile> screens)
    {
        return screens.Count(screen => screen.CreatedByIpc);
    }

    public static bool CanCreateUserScreen(IReadOnlyCollection<BrowserScreenProfile> screens)
    {
        return screens.Count < Configuration.MaxRenderableBrowserScreens
            && CountUserScreens(screens) < Configuration.MaxBrowserScreens;
    }

    public static bool CanCreateIpcScreen(IReadOnlyCollection<BrowserScreenProfile> screens)
    {
        return screens.Count < Configuration.MaxRenderableBrowserScreens
            && CountIpcScreens(screens) < Configuration.MaxIpcBrowserScreens;
    }
}
