using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrystalCast.Video;

internal sealed record LinuxBrowserEndpoint(int Port, string Token, string Directory, IDisposable? Launcher = null)
{
    public static LinuxBrowserEndpoint Read(string path, IDisposable? launcher = null)
    {
        if (new FileInfo(path).Length > 4096)
            throw new InvalidDataException("Native browser endpoint exceeds the size limit.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        var port = root.GetProperty("port").GetInt32();
        var token = root.GetProperty("token").GetString() ?? "";
        if (root.GetProperty("version").GetInt32() != 1 || port is < 1 or > 65535
            || !Regex.IsMatch(token, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid native browser endpoint.");
        return new LinuxBrowserEndpoint(port, token, Path.GetDirectoryName(path)!, launcher);
    }
}

internal interface ILinuxBrowserLauncher
{
    Task<LinuxBrowserEndpoint> LaunchAsync(CancellationToken cancellationToken);
}

internal sealed class LinuxBrowserLauncher(string? executableOverride = null) : ILinuxBrowserLauncher
{
    public static string ToWinePath(string nativePath)
    {
        if (!nativePath.StartsWith('/') || nativePath.Contains('\0') || nativePath.Contains('\\')
            || nativePath.Split('/').Any(component => component is ".." or "."))
            throw new InvalidDataException("Native browser paths must be absolute Linux paths.");
        return "Z:" + nativePath.Replace('/', '\\');
    }

    public static string? FindHelper()
    {
        var installed = LinuxBrowserRuntimeInstaller.InstalledEntrypoint();
        if (installed != null) return installed;
        var home = Environment.GetEnvironmentVariable("HOME");
        string?[] candidates =
        [
            string.IsNullOrEmpty(home) ? null : home.TrimEnd('/') + "/.local/bin/crystalcast-browser-helper",
            "/usr/local/bin/crystalcast-browser-helper", "/usr/bin/crystalcast-browser-helper",
        ];
        return candidates.FirstOrDefault(path => path != null && File.Exists(ToWinePath(path)));
    }

    public async Task<LinuxBrowserEndpoint> LaunchAsync(CancellationToken cancellationToken)
    {
        if (!WineEnvironment.IsWine)
            throw new PlatformNotSupportedException("The native Linux browser launcher is only available under Wine.");
        var executable = executableOverride ?? FindHelper() ?? throw new FileNotFoundException(
            "Linux Browser is not installed. Open Linux Browser Setup to install a verified runtime when available.");
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime))
            throw new InvalidOperationException("XDG_RUNTIME_DIR is missing; start the game from your Linux desktop session.");
        var session = Guid.NewGuid().ToString("N");
        var endpointPath = ToWinePath(runtime.TrimEnd('/') + "/crystalcast/" + session + "/endpoint.json");
        var start = new ProcessStartInfo("start.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.Environment.Remove("LD_PRELOAD");
        start.Environment.Remove("LD_AUDIT");
        start.Environment.Remove("LD_LIBRARY_PATH");
        start.ArgumentList.Add("/wait");
        start.ArgumentList.Add("/unix");
        start.ArgumentList.Add(executable);
        start.ArgumentList.Add("--session");
        start.ArgumentList.Add(session);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Wine could not start the native browser helper.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(endpointPath))
                {
                    try { return LinuxBrowserEndpoint.Read(endpointPath, process); }
                    catch (JsonException) { /* The helper may still be publishing its endpoint. */ }
                }
                // Wine can finish start.exe before its native Unix child. The
                // authenticated endpoint, rather than that wrapper's lifetime,
                // establishes whether the helper started successfully.
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        catch { process.Dispose(); throw; }
        // The helper times out unconnected startup itself. Once connected,
        // socket EOF/heartbeat expiry shuts it and its browser children down.
    }
}
