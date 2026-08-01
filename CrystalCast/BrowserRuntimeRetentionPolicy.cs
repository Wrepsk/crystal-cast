namespace CrystalCast;

internal static class BrowserRuntimeRetentionPolicy
{
    public static IReadOnlySet<string> GetRetainedScreenIds(
        bool pluginEnabled,
        IEnumerable<BrowserScreenProfile> screens)
    {
        if (!pluginEnabled)
            return new HashSet<string>(StringComparer.Ordinal);

        return ScreenLimitPolicy.GetActiveScreens(screens)
            .Select(screen => screen.ScreenId)
            .ToHashSet(StringComparer.Ordinal);
    }
}
