namespace CrystalCast.Video;

internal static class FfmpegLocator
{
    public static string? ResolveFfmpegPath(string configuredPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath) ? "ffmpeg.exe" : configuredPath.Trim();
        if (Path.IsPathFullyQualified(candidate))
            return File.Exists(candidate) ? candidate : null;

        if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
        {
            foreach (var baseDirectory in GetSearchDirectories())
            {
                var rooted = Path.GetFullPath(Path.Combine(baseDirectory, candidate));
                if (File.Exists(rooted))
                    return rooted;
            }

            return null;
        }

        var executableNames = GetExecutableNames(candidate);
        foreach (var directory in GetSearchDirectories())
        {
            foreach (var executableName in executableNames)
            {
                var path = Path.Combine(directory, executableName);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    public static string ResolveWorkingDirectory(string inputPath)
    {
        var videoDirectory = Path.GetDirectoryName(inputPath);
        if (!string.IsNullOrWhiteSpace(videoDirectory) && Directory.Exists(videoDirectory))
            return videoDirectory;

        return Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static string[] GetExecutableNames(string candidate)
    {
        if (Path.GetExtension(candidate).Length > 0)
            return [candidate];

        return ["ffmpeg.exe", "ffmpeg.cmd", "ffmpeg.bat", "ffmpeg"];
    }

    private static IEnumerable<string> GetSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in GetPreferredSearchDirectories())
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && seen.Add(directory))
                yield return directory;
        }

        foreach (var directory in GetPathDirectories())
        {
            if (Directory.Exists(directory) && seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string> GetPathDirectories()
    {
        foreach (var target in new[]
        {
            EnvironmentVariableTarget.Process,
            EnvironmentVariableTarget.User,
            EnvironmentVariableTarget.Machine,
        })
        {
            var path = Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = Environment.ExpandEnvironmentVariables(directory);
                if (!string.IsNullOrWhiteSpace(expanded))
                    yield return expanded;
            }
        }
    }

    private static IEnumerable<string> GetPreferredSearchDirectories()
    {
        var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName;
        if (!string.IsNullOrWhiteSpace(pluginDirectory))
        {
            yield return pluginDirectory;
            yield return Path.Combine(pluginDirectory, "ffmpeg");
            yield return Path.Combine(pluginDirectory, "ffmpeg", "bin");
        }

        yield return AppContext.BaseDirectory;
        yield return @"C:\ffmpeg\bin";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "chocolatey",
            "bin");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "scoop",
            "shims");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Links");
    }
}
