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

    public static IReadOnlyList<BrowserScreenProfile> GetAllowedScreens(IEnumerable<BrowserScreenProfile> screens)
    {
        var allowed = new List<BrowserScreenProfile>(Configuration.MaxRenderableBrowserScreens);
        var userCount = 0;
        var ipcCount = 0;

        foreach (var screen in screens)
        {
            if (allowed.Count >= Configuration.MaxRenderableBrowserScreens)
                break;

            if (screen.CreatedByIpc)
            {
                if (ipcCount >= Configuration.MaxIpcBrowserScreens)
                    continue;

                ipcCount++;
            }
            else
            {
                if (userCount >= Configuration.MaxBrowserScreens)
                    continue;

                userCount++;
            }

            allowed.Add(screen);
        }

        return allowed;
    }

    public static bool DisableScreensOutsideLimits(IReadOnlyList<BrowserScreenProfile> screens)
    {
        var allowedIds = GetAllowedScreens(screens)
            .Select(screen => screen.ScreenId)
            .ToHashSet(StringComparer.Ordinal);
        var changed = false;

        foreach (var screen in screens)
        {
            if (!screen.Enabled || allowedIds.Contains(screen.ScreenId))
                continue;

            screen.Enabled = false;
            changed = true;
        }

        return changed;
    }
}
