using Microsoft.Web.WebView2.Core;

namespace CrystalCast.Browser.WebView2;

/// <summary>Which WebView2 browser CrystalCast found: the system-wide one or the one it downloaded.</summary>
internal readonly record struct WebView2RuntimeInfo(string Version, string? BrowserExecutableFolder)
{
    public bool IsDownloaded => BrowserExecutableFolder != null;

    public string Describe() => IsDownloaded ? $"{Version} (downloaded by CrystalCast)" : Version;
}

/// <summary>
/// Finds WebView2. The installed (Evergreen) runtime is used when there is one; otherwise the Fixed Version
/// runtime that CrystalCast downloaded into its own folder. The Fixed Version is a plain folder, so it needs
/// no installer or update service and works the same in any Wine prefix, Proton or container.
/// </summary>
internal static class WebView2FixedRuntime
{
    /// <summary>Written last, so a folder without it is an unfinished download.</summary>
    internal const string CompleteMarkerName = ".crystalcast-complete";
    internal const string BrowserExecutableName = "msedgewebview2.exe";

    /// <summary>Files the game still had loaded when the runtime was deleted; emptied on the next load.</summary>
    internal const string TrashFolderName = ".trash";

    private static readonly object Gate = new();
    private static string? cachedFolder;
    private static volatile bool removing;
    private static long cachedSizeBytes = -1;
    private static long cachedSizeAtTick;

    /// <summary>Next to the browser profiles, not inside them, so clearing browser data keeps the runtime.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrystalCast",
        "WebView2Runtime");

    /// <summary>Finds a usable runtime. Never throws; <paramref name="error"/> explains a miss.</summary>
    public static bool TryFind(out WebView2RuntimeInfo runtime, out string error)
    {
        runtime = default;
        string installedError;
        try
        {
            var installed = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrWhiteSpace(installed))
            {
                runtime = new WebView2RuntimeInfo(installed, null);
                error = string.Empty;
                return true;
            }

            installedError = "Microsoft Edge WebView2 Runtime is not installed";
        }
        catch (Exception ex)
        {
            installedError = $"Microsoft Edge WebView2 Runtime unavailable: {ex.Message}";
        }

        var folder = FindDownloadedFolder();
        if (folder != null)
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString(folder);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    runtime = new WebView2RuntimeInfo(version, folder);
                    error = string.Empty;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = $"{installedError}; the downloaded WebView2 in {folder} could not be used: {ex.Message}";
                return false;
            }
        }

        error = installedError;
        return false;
    }

    /// <summary>
    /// While set, the download is treated as missing so browser screens don't start it again while its
    /// files are being deleted.
    /// </summary>
    public static bool Removing
    {
        get => removing;
        set
        {
            removing = value;
            Invalidate();
        }
    }

    /// <summary>The newest finished download, or null. Cheap enough to call every frame.</summary>
    public static string? FindDownloadedFolder()
    {
        if (removing)
            return null;

        lock (Gate)
        {
            if (cachedFolder != null && IsComplete(cachedFolder))
                return cachedFolder;

            cachedFolder = null;
            try
            {
                if (!Directory.Exists(Root))
                    return null;

                cachedFolder = Directory.EnumerateDirectories(Root)
                    .Where(IsComplete)
                    .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
                    .Where(entry => entry.Version != null)
                    .OrderByDescending(entry => entry.Version)
                    .Select(entry => entry.Path)
                    .FirstOrDefault();
            }
            catch
            {
                cachedFolder = null;
            }

            return cachedFolder;
        }
    }

    /// <summary>Forgets the cached folder after a download finished or was removed.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            cachedFolder = null;
            cachedSizeBytes = -1;
        }
    }

    /// <summary>Disk space used by downloaded runtimes, including unfinished ones. Re-measured every few seconds.</summary>
    public static long GetDownloadedBytes()
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (cachedSizeBytes >= 0 && now - cachedSizeAtTick < 5000)
                return cachedSizeBytes;

            long total = 0;
            try
            {
                if (Directory.Exists(Root))
                {
                    var root = new DirectoryInfo(Root);
                    total = root.EnumerateFiles().Sum(file => file.Length)
                        + root.EnumerateDirectories()
                            .Where(folder => !IsTrashFolder(folder.FullName))
                            .SelectMany(folder => folder.EnumerateFiles("*", SearchOption.AllDirectories))
                            .Sum(file => file.Length);
                }
            }
            catch
            {
                // Files can disappear while a download or deletion runs; the next measurement catches up.
            }

            cachedSizeBytes = total;
            cachedSizeAtTick = now;
            return total;
        }
    }

    internal static bool IsTrashFolder(string folder) =>
        string.Equals(Path.GetFileName(folder.TrimEnd('\\', '/')), TrashFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes files left behind by an earlier delete. Call at plugin load, before any WebView2 browser starts,
    /// while nothing has them loaded. Never throws; returns a line for the log, or null when nothing was there.
    /// </summary>
    public static string? EmptyTrash()
    {
        var trash = Path.Combine(Root, TrashFolderName);
        try
        {
            if (!Directory.Exists(trash))
                return null;

            Directory.Delete(trash, recursive: true);
            if (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any())
                Directory.Delete(Root);
            return "Removed the rest of the previously deleted WebView2 download.";
        }
        catch (Exception ex)
        {
            return $"Could not remove the rest of the previously deleted WebView2 download yet: {ex.Message}";
        }
    }

    internal static bool IsComplete(string folder)
    {
        return File.Exists(Path.Combine(folder, CompleteMarkerName))
            && File.Exists(Path.Combine(folder, BrowserExecutableName));
    }

    internal static Version? ParseVersion(string? text)
    {
        return Version.TryParse(text, out var version) && version.Build >= 0 && version.Revision >= 0 ? version : null;
    }
}
