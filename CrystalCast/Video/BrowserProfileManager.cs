namespace CrystalCast.Video;

internal static class BrowserProfileManager
{
    private const string ClearRequestFileName = "clear-browser-data.requested";
    private static readonly string[] LegacyProfileDirectoryNames =
    [
        "WebView2",
        "WebView2Dailymotion",
        "WebView2Vimeo",
        "WebView2GenericWeb",
        "Cef",
    ];

    public static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrystalCast",
        "BrowserProfiles");

    public static string GetWebView2UserDataFolder(BrowserSourceProviderKind providerKind)
    {
        return Path.Combine(ProfileRoot, "WebView2", providerKind.ToString());
    }

    public static string RequestClearOnNextStart()
    {
        Directory.CreateDirectory(ProfileRoot);
        File.WriteAllText(Path.Combine(ProfileRoot, ClearRequestFileName), DateTimeOffset.UtcNow.ToString("O"));
        return "Browser data will be cleared the next time CrystalCast loads.";
    }

    public static string ApplyPendingClearRequest()
    {
        var requestPath = Path.Combine(ProfileRoot, ClearRequestFileName);
        if (!File.Exists(requestPath))
            return string.Empty;

        var failures = new List<string>();
        foreach (var directory in GetKnownProfileDirectories())
        {
            try
            {
                DeleteKnownProfileDirectory(directory);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(directory)}: {ex.GetBaseException().Message}");
            }
        }

        if (failures.Count > 0)
            return $"Browser data clear is still pending: {string.Join("; ", failures)}";

        try
        {
            File.Delete(requestPath);
            return "Browser data was cleared.";
        }
        catch (Exception ex)
        {
            return $"Browser data was cleared, but the request marker could not be removed: {ex.GetBaseException().Message}";
        }
    }

    internal static IReadOnlyList<string> GetKnownProfileDirectories()
    {
        var applicationRoot = Path.GetDirectoryName(ProfileRoot)
            ?? throw new InvalidOperationException("CrystalCast profile root is invalid.");
        var directories = new List<string>
        {
            Path.Combine(ProfileRoot, "WebView2"),
        };
        directories.AddRange(LegacyProfileDirectoryNames.Select(name => Path.Combine(applicationRoot, name)));
        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void DeleteKnownProfileDirectory(string directory)
    {
        var applicationRoot = Path.GetFullPath(Path.GetDirectoryName(ProfileRoot)!)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(applicationRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to clear a browser profile outside CrystalCast storage.");

        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}
