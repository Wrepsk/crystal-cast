namespace CrystalCast;

internal static class PluginVersion
{
    /// <summary>The plugin version for the UI, such as "v0.9.4.2", without a trailing ".0".</summary>
    public static string Label { get; } = Format(typeof(PluginVersion).Assembly.GetName().Version);

    internal static string Format(Version? version)
    {
        if (version is null)
            return string.Empty;

        return "v" + (version.Revision > 0 ? version.ToString(4) : version.ToString(3));
    }
}
