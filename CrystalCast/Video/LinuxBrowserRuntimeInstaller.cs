using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalCast.Video;

internal sealed record LinuxRuntimeInstallProgress(string Message, double Fraction);
internal sealed record LinuxRuntimeInstallation(string Version, string Engine, string Directory, string Entrypoint);

internal sealed class LinuxBrowserRuntimeInstaller
{
    public static string NativeRoot
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data))
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home)) throw new IOException("The Linux launcher did not provide HOME or XDG_DATA_HOME.");
                data = home.TrimEnd('/') + "/.local/share";
            }
            LinuxBrowserLauncher.ToWinePath(data);
            return data.TrimEnd('/') + "/crystalcast/browser";
        }
    }

    public static LinuxRuntimeInstallation? Installed()
    {
        if (!WineEnvironment.IsWine) return null;
        var root = LinuxBrowserLauncher.ToWinePath(NativeRoot);
        var receipt = Path.Combine(root, "current.json");
        if (!File.Exists(receipt)) return null;
        LinuxBrowserRuntimePackage.RejectLinks(receipt);
        if (new FileInfo(receipt).Length > 4096) throw new InvalidDataException("Invalid browser installation receipt.");
        var installation = JsonSerializer.Deserialize<LinuxRuntimeInstallation>(File.ReadAllBytes(receipt), LinuxBrowserRuntimePackage.JsonOptions)
            ?? throw new InvalidDataException("Invalid browser installation receipt.");
        if (!LinuxBrowserRuntimePackage.IsVersion(installation.Version)
            || !System.Text.RegularExpressions.Regex.IsMatch(installation.Directory, "\\A[0-9]+\\.[0-9]+\\.[0-9]+-[0-9a-f]{32}\\z")
            || installation.Entrypoint != "bin/crystalcast-browser") throw new InvalidDataException("Invalid installed browser paths.");
        var executable = LinuxBrowserRuntimePackage.SafeFilePath(root, installation.Directory + "/" + installation.Entrypoint);
        LinuxBrowserRuntimePackage.RejectLinks(executable);
        return File.Exists(executable) ? installation : null;
    }

    public static string? InstalledEntrypoint()
    {
        var installation = Installed();
        return installation == null ? null : NativeRoot + "/" + installation.Directory + "/" + installation.Entrypoint;
    }

    // A build may contain no release until a tested runtime and matching source
    // archives are published. Never substitute an unpinned "latest" download.
    public static string? AutomaticInstallBlocker()
    {
        if (!WineEnvironment.IsWine) return "Automatic Linux Browser installation is available only under Wine.";
        if (File.Exists(LinuxBrowserLauncher.ToWinePath("/.flatpak-info")))
            return "This launcher runs inside Flatpak. Automatic installation needs a verified host-launch bridge and is not available for this environment yet.";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"))
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP_NAME"))
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP_REVISION")))
            return "This launcher runs inside Snap. Automatic installation needs a verified host-launch bridge and is not available for this environment yet.";
        if (Directory.Exists(LinuxBrowserLauncher.ToWinePath("/run/pressure-vessel"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PRESSURE_VESSEL_RUNTIME")))
            return "This launcher runs inside a Steam runtime container. Its libraries can differ from the host distribution; automatic installation is not verified for this environment yet.";
        return null;
    }

    public static LinuxRuntimeRelease? SelectRelease(LinuxDistribution distribution)
    {
        if (!WineEnvironment.IsWine || !Environment.Is64BitProcess || distribution.Id.Length == 0 || AutomaticInstallBlocker() != null) return null;
        return LinuxRuntimeCatalog.Load().Releases.FirstOrDefault(release =>
            release.TestedDistributions.Contains(distribution.Id + ":" + distribution.Version, StringComparer.Ordinal)
            || release.TestedDistributions.Contains(distribution.Id + ":*", StringComparer.Ordinal));
    }

    public async Task<LinuxRuntimeInstallation> InstallAsync(LinuxRuntimeRelease release,
        IProgress<LinuxRuntimeInstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        cancellationToken = deadline.Token;
        if (!WineEnvironment.IsWine) throw new PlatformNotSupportedException("Linux browser installation requires Wine.");
        if (AutomaticInstallBlocker() is { } blocker) throw new PlatformNotSupportedException(blocker);
        if (!LinuxBrowserRuntimePackage.IsVersion(release.Version) || !LinuxBrowserRuntimePackage.IsHash(release.ArchiveSha256)
            || release.ArchiveBytes is <= 0 or > LinuxBrowserRuntimePackage.MaximumArchiveBytes)
            throw new InvalidDataException("Invalid trusted Linux browser catalog entry.");
        var nativeRoot = NativeRoot;
        var root = LinuxBrowserLauncher.ToWinePath(nativeRoot);
        LinuxBrowserRuntimePackage.RejectLinks(root);
        Directory.CreateDirectory(root);
        // Serialize across setup windows and plugin instances sharing the same directory.
        using var installLock = new FileStream(Path.Combine(root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var id = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(root, "download-" + id + ".zip");
        var directoryName = release.Version + "-" + id;
        var directory = Path.Combine(root, directoryName);
        var activated = false;
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CrystalCast-LinuxBrowser/1");
            progress?.Report(new("Downloading the trusted runtime manifest...", 0));
            using var response = await GetAsync(http, release.ManifestUrl, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("Browser manifest is too large.");
            var manifestBytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var manifest = LinuxBrowserRuntimePackage.ReadManifest(manifestBytes, release);
            using (var package = await GetAsync(http, release.ArchiveUrl, cancellationToken).ConfigureAwait(false))
            {
                if (package.Content.Headers.ContentLength is { } length && length != release.ArchiveBytes)
                    throw new InvalidDataException("Linux browser download has the wrong size.");
                using var input = await package.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long downloaded = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    downloaded += count;
                    if (downloaded > release.ArchiveBytes) throw new InvalidDataException("Browser download exceeds its trusted size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new($"Downloading Linux Browser: {downloaded / 1048576.0:0.0} / {release.ArchiveBytes / 1048576.0:0.0} MiB", .7 * downloaded / release.ArchiveBytes));
                }
                if (downloaded != release.ArchiveBytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.ArchiveSha256)))
                    throw new InvalidDataException("Linux browser archive failed SHA-256 verification.");
            }
            using (var archive = File.OpenRead(archivePath))
                await LinuxBrowserRuntimePackage.ExtractVerifiedAsync(archive, manifest, directory,
                    new InlineProgress<double>(fraction => progress?.Report(new("Verifying and unpacking Linux Browser...", .7 + fraction * .2))), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(directory, "runtime-manifest.json"), manifestBytes, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Preparing executable permissions...", .92));
            await SetPermissionsAsync(nativeRoot + "/" + directoryName, manifest, cancellationToken).ConfigureAwait(false);
            var installation = new LinuxRuntimeInstallation(manifest.Version, manifest.Engine, directoryName, manifest.Entrypoint);
            var receipt = JsonSerializer.SerializeToUtf8Bytes(installation);
            progress?.Report(new("Checking the installed browser and sandbox startup...", .96));
            // Probe this candidate directly. Other screens continue seeing the
            // previous receipt until the candidate has rendered successfully.
            var candidate = new LinuxBrowserLauncher(nativeRoot + "/" + directoryName + "/" + manifest.Entrypoint);
            var ready = await new LinuxBrowserAvailabilityProbe(candidate).CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Available) throw new IOException(ready.Message);
            await WriteReceiptAsync(root, receipt, cancellationToken).ConfigureAwait(false);
            activated = true;
            progress?.Report(new($"Linux Browser {installation.Version} installed. {ready.Message}", 1));
            return installation;
        }
        catch
        {
            if (!activated && Directory.Exists(directory))
            {
                LinuxBrowserRuntimePackage.RejectLinks(directory);
                Directory.Delete(directory, recursive: true);
            }
            throw;
        }
        finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    }

    private static async Task WriteReceiptAsync(string root, byte[] receipt, CancellationToken token)
    {
        var temporary = Path.Combine(root, "receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, receipt, token).ConfigureAwait(false);
            File.Move(temporary, Path.Combine(root, "current.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task SetPermissionsAsync(string nativeDirectory, LinuxRuntimeManifest manifest, CancellationToken token)
    {
        // ZIP extraction under Windows .NET/Wine does not preserve Unix mode.
        // Run the host chmod with separate arguments, never a constructed shell command.
        await ChmodAsync(["-R", "u+rwX,go-rwx", "--", nativeDirectory], token).ConfigureAwait(false);
        var files = manifest.Files.Where(file => file.Executable).Select(file => nativeDirectory + "/" + file.Path).ToArray();
        foreach (var chunk in files.Chunk(64))
            await ChmodAsync(new[] { "u+x", "--" }.Concat(chunk), token).ConfigureAwait(false);
    }

    private static async Task ChmodAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        var nativeAck = NativeRoot + "/permissions-" + Guid.NewGuid().ToString("N") + ".ack";
        var ack = LinuxBrowserLauncher.ToWinePath(nativeAck);
        var start = new ProcessStartInfo("start.exe") { UseShellExecute = false, CreateNoWindow = true };
        // Wine's wrapper may finish before its Unix child. The script is fixed;
        // paths and modes are separate arguments, never interpolated shell code.
        const string script = "ack=$1; shift; /bin/chmod \"$@\" && printf ok > \"$ack\"";
        foreach (var arg in new[] { "/wait", "/unix", "/bin/sh", "-c", script, "crystalcast-permissions", nativeAck }.Concat(arguments)) start.ArgumentList.Add(arg);
        start.Environment.Remove("LD_PRELOAD"); start.Environment.Remove("LD_AUDIT"); start.Environment.Remove("LD_LIBRARY_PATH");
        using var process = Process.Start(start) ?? throw new IOException("Wine could not prepare native browser permissions.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (!File.Exists(ack) || await File.ReadAllTextAsync(ack, deadline.Token).ConfigureAwait(false) != "ok")
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("Wine could not confirm native browser executable permissions. Check launcher access under Advanced.");
        }
        finally { if (File.Exists(ack)) File.Delete(ack); }
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken token)
    {
        var uri = new Uri(url);
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !IsDownloadHost(uri.Host)
                || uri.IsDefaultPort == false) throw new InvalidDataException("Untrusted Linux browser download address.");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location); response.Dispose(); continue;
            }
            if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new HttpRequestException($"Linux browser download failed (HTTP {status})."); }
            return response;
        }
        throw new HttpRequestException("Too many Linux browser download redirects.");
    }

    private static bool IsDownloadHost(string host) => host is "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    private static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken token)
    {
        using (input)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("Browser manifest exceeds the size limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}
