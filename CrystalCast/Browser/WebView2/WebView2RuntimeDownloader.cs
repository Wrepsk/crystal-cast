using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace CrystalCast.Video;

internal enum WebView2DownloadStage
{
    Idle,
    FindingLatest,
    Downloading,
    Verifying,
    Unpacking,
    Finishing,
    Done,
    Failed,
    Cancelled,
    Removing,
    Removed,
    RemoveFailed,
}

/// <summary>One snapshot of a download, for the UI. Byte counts are 0 when unknown.</summary>
internal sealed record WebView2DownloadProgress(
    WebView2DownloadStage Stage,
    string Message,
    long DoneBytes = 0,
    long TotalBytes = 0)
{
    public bool IsBusy => Stage is not (WebView2DownloadStage.Idle or WebView2DownloadStage.Done
        or WebView2DownloadStage.Failed or WebView2DownloadStage.Cancelled
        or WebView2DownloadStage.Removed or WebView2DownloadStage.RemoveFailed);

    public float Fraction => TotalBytes > 0 ? Math.Clamp((float)DoneBytes / TotalBytes, 0f, 1f) : 0f;
}

internal sealed record WebView2FixedRuntimeLink(Version Version, Uri Url);

/// <summary>
/// Downloads Microsoft's WebView2 Fixed Version runtime for x64 into <see cref="WebView2FixedRuntime.Root"/>.
/// The download link comes from Microsoft's WebView2 download page, with a built-in link as a fallback; only
/// HTTPS links on Microsoft's download host are accepted. Under Wine it also sets the Windows 7 mode that
/// the WebView2 browser needs to start (Wine bug 58921), as winetricks does.
/// </summary>
internal sealed class WebView2RuntimeDownloader : IDisposable
{
    internal const string DownloadPage = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";
    internal const string DownloadHost = "msedge.sf.dl.delivery.mp.microsoft.com";

    /// <summary>Used when the download page cannot be read. Microsoft keeps the last two major versions online.</summary>
    internal static readonly WebView2FixedRuntimeLink FallbackLink = new(
        new Version(157, 0, 4341, 42),
        new Uri("https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/b7b22a57-b321-483e-9855-d260b076f1a7/Microsoft.WebView2.FixedVersionRuntime.157.0.4341.42.x64.cab"));

    /// <summary>The download plus the unpacked copy: about 310 MB and 700 MB today, with headroom.</summary>
    internal const long RequiredFreeBytes = 1_300L * 1024 * 1024;

    private static readonly Regex CabLinkPattern = new(
        @"https://msedge\.sf\.dl\.delivery\.mp\.microsoft\.com/filestreamingservice/files/[0-9a-fA-F-]{36}/Microsoft\.WebView2\.FixedVersionRuntime\.(?<version>\d+\.\d+\.\d+\.\d+)\.x64\.cab",
        RegexOptions.CultureInvariant);

    private readonly Action<string> log;
    private readonly bool isWine;
    private CancellationTokenSource? cancellation;
    private volatile WebView2DownloadProgress progress = new(WebView2DownloadStage.Idle, string.Empty);

    public WebView2RuntimeDownloader(Action<string> log, bool isWine)
    {
        this.log = log;
        this.isWine = isWine;
    }

    public WebView2DownloadProgress Progress => progress;

    /// <summary>Starts a download in the background unless one is already running.</summary>
    public void Start()
    {
        if (progress.IsBusy)
            return;

        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Report(WebView2DownloadStage.FindingLatest, "Finding the latest WebView2 download");
        _ = Task.Run(() => RunAsync(token), token);
    }

    public void Cancel()
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    /// <summary>
    /// Deletes every downloaded runtime. <paramref name="closeBrowsers"/> runs first, on the caller's thread,
    /// and must close all WebView2 browsers; the files are then deleted in the background, retrying while
    /// the browser processes exit.
    /// </summary>
    public void StartRemoval(Action closeBrowsers)
    {
        if (progress.IsBusy)
            return;

        WebView2FixedRuntime.Removing = true;
        Report(WebView2DownloadStage.Removing, "Closing browser screens");
        try
        {
            closeBrowsers();
        }
        catch (Exception ex)
        {
            log($"Closing browser screens before deleting WebView2 failed: {ex.Message}");
        }

        _ = Task.Run(RemoveAsync);
    }

    private async Task RemoveAsync()
    {
        var root = WebView2FixedRuntime.Root;
        var before = WebView2FixedRuntime.GetDownloadedBytes();
        try
        {
            Report(WebView2DownloadStage.Removing, "Deleting the downloaded WebView2");
            var folders = Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Where(folder => !WebView2FixedRuntime.IsTrashFolder(folder)).ToList()
                : [];

            // Remove the markers first, so a partly deleted folder is never used again.
            foreach (var folder in folders)
                TryDelete(Path.Combine(folder, WebView2FixedRuntime.CompleteMarkerName), isFolder: false);

            // Browser processes keep their files open for a moment after closing.
            for (var attempt = 0; attempt < 6 && folders.Any(Directory.Exists); attempt++)
            {
                foreach (var folder in folders)
                    TryDelete(folder, isFolder: true);
                if (folders.Any(Directory.Exists))
                    await Task.Delay(500);
            }

            // The game process itself keeps EmbeddedBrowserWebView.dll loaded once a browser has started, and
            // a loaded file can't be deleted, but it can be moved. Move what is left to the trash folder,
            // which is emptied the next time CrystalCast loads, before any browser starts.
            var trashedBytes = 0L;
            foreach (var folder in folders.Where(Directory.Exists))
                trashedBytes += MoveToTrash(folder);
            foreach (var folder in folders)
                TryDelete(folder, isFolder: true);

            var stillThere = folders.Where(Directory.Exists).ToList();
            WebView2FixedRuntime.Invalidate();
            if (stillThere.Count > 0)
            {
                throw new IOException(
                    $"Some files are still in use in {string.Join(", ", stillThere)}. Restart the game and delete it again.");
            }

            var freed = Math.Max(0, before - trashedBytes);
            var text = trashedBytes > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Deleted the downloaded WebView2 and freed {freed / (1024 * 1024)} MB. The game still has {Math.Max(1, trashedBytes / (1024 * 1024))} MB of it loaded; that part is removed the next time the game starts.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Deleted the downloaded WebView2 and freed {freed / (1024 * 1024)} MB.");
            log(text);
            Report(WebView2DownloadStage.Removed, text);
        }
        catch (Exception ex)
        {
            log($"Deleting the downloaded WebView2 failed: {ex}");
            Report(WebView2DownloadStage.RemoveFailed, ex.GetBaseException().Message);
        }
        finally
        {
            WebView2FixedRuntime.Removing = false;
        }
    }

    /// <summary>Picks the newest x64 Fixed Version link from the download page's HTML.</summary>
    internal static WebView2FixedRuntimeLink? FindNewestLink(string html)
    {
        var text = html.Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase);
        WebView2FixedRuntimeLink? newest = null;
        foreach (Match match in CabLinkPattern.Matches(text))
        {
            var version = WebView2FixedRuntime.ParseVersion(match.Groups["version"].Value);
            if (version == null || !IsAllowedLink(match.Value))
                continue;

            if (newest == null || version > newest.Version)
                newest = new WebView2FixedRuntimeLink(version, new Uri(match.Value));
        }

        return newest;
    }

    internal static bool IsAllowedLink(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, DownloadHost, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.EndsWith(".x64.cab", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maps a path inside the cabinet into the target folder, dropping the cabinet's top folder.</summary>
    internal static string? MapCabinetPath(string nameInCabinet, string targetFolder)
    {
        var parts = nameInCabinet.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Any(part => part is "." or ".."))
            return null;

        var relative = Path.Combine(parts[1..]);
        var full = Path.GetFullPath(Path.Combine(targetFolder, relative));
        var root = Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        var root = WebView2FixedRuntime.Root;
        string? cabinetPath = null;
        string? stagingFolder = null;
        try
        {
            Directory.CreateDirectory(root);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CrystalCast-WebView2-Setup/1.0");

            var link = await FindLinkAsync(http, token);
            var finalFolder = Path.Combine(root, link.Version.ToString());
            if (WebView2FixedRuntime.IsComplete(finalFolder))
            {
                PrepareWine();
                WebView2FixedRuntime.Invalidate();
                Report(WebView2DownloadStage.Done, $"WebView2 {link.Version} is already downloaded.");
                return;
            }

            CheckFreeSpace(root);
            cabinetPath = Path.Combine(root, $"download-{link.Version}.cab");
            await DownloadAsync(http, link, cabinetPath, token);

            Report(WebView2DownloadStage.Verifying, "Checking Microsoft's signature");
            var signature = AuthenticodeVerifier.Verify(cabinetPath, out var status);
            var signatureText = AuthenticodeVerifier.Describe(status);
            log($"WebView2 download signature: {signatureText}.");
            if (signature == AuthenticodeResult.Invalid
                || (signature == AuthenticodeResult.UntrustedRoot && !isWine))
            {
                throw new InvalidDataException($"The download failed its signature check: {signatureText}.");
            }

            stagingFolder = Path.Combine(root, $"unpacking-{link.Version}");
            if (Directory.Exists(stagingFolder))
                Directory.Delete(stagingFolder, recursive: true);
            Directory.CreateDirectory(stagingFolder);
            var cabinetSize = new FileInfo(cabinetPath).Length;
            Report(WebView2DownloadStage.Unpacking, "Unpacking", 0, cabinetSize * 9 / 4);
            var staging = stagingFolder;
            CabinetExtractor.Extract(
                cabinetPath,
                name => MapCabinetPath(name, staging),
                bytes => Report(WebView2DownloadStage.Unpacking, $"Unpacking ({bytes / (1024 * 1024)} MB)", Math.Min(bytes, cabinetSize * 9 / 4 - 1), cabinetSize * 9 / 4),
                token);

            Report(WebView2DownloadStage.Finishing, "Finishing setup");
            if (!File.Exists(Path.Combine(stagingFolder, WebView2FixedRuntime.BrowserExecutableName)))
                throw new InvalidDataException($"The download has no {WebView2FixedRuntime.BrowserExecutableName}.");

            PrepareWine();
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(stagingFolder);
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidDataException("WebView2 does not recognize the unpacked browser.");

            if (Directory.Exists(finalFolder))
                Directory.Delete(finalFolder, recursive: true);
            Directory.Move(stagingFolder, finalFolder);
            stagingFolder = null;
            File.WriteAllText(
                Path.Combine(finalFolder, WebView2FixedRuntime.CompleteMarkerName),
                string.Create(CultureInfo.InvariantCulture, $"{version}\n{link.Url}\n{signatureText}\n{DateTimeOffset.UtcNow:O}\n"));
            WebView2FixedRuntime.Invalidate();
            RemoveOlderVersions(root, finalFolder);
            log($"WebView2 {version} downloaded to {finalFolder}.");
            Report(WebView2DownloadStage.Done, $"WebView2 {version} is ready.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Report(WebView2DownloadStage.Cancelled, "Download cancelled.");
        }
        catch (Exception ex)
        {
            log($"WebView2 download failed: {ex}");
            Report(WebView2DownloadStage.Failed, ex.GetBaseException().Message);
        }
        finally
        {
            TryDelete(cabinetPath, isFolder: false);
            TryDelete(cabinetPath + ".part", isFolder: false);
            TryDelete(stagingFolder, isFolder: true);
        }
    }

    private async Task<WebView2FixedRuntimeLink> FindLinkAsync(HttpClient http, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var html = await http.GetStringAsync(new Uri(DownloadPage), timeout.Token);
            var newest = FindNewestLink(html);
            if (newest != null)
            {
                log($"WebView2 download page lists {newest.Version}.");
                return newest.Version >= FallbackLink.Version ? newest : FallbackLink;
            }

            log("WebView2 download page had no x64 Fixed Version link; using the built-in link.");
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            log($"Could not read the WebView2 download page ({ex.GetBaseException().Message}); using the built-in link.");
        }

        return FallbackLink;
    }

    private async Task DownloadAsync(HttpClient http, WebView2FixedRuntimeLink link, string cabinetPath, CancellationToken token)
    {
        var partPath = cabinetPath + ".part";
        Report(WebView2DownloadStage.Downloading, $"Downloading WebView2 {link.Version}");
        using var response = await http.GetAsync(link.Url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? $"Microsoft no longer offers WebView2 {link.Version} at the known link (HTTP 404). An update of CrystalCast may be needed."
                : $"The download failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            var lastReport = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                var now = Environment.TickCount64;
                if (now - lastReport >= 250)
                {
                    lastReport = now;
                    Report(WebView2DownloadStage.Downloading, $"Downloading WebView2 {link.Version}", done, total);
                }
            }

            if (total > 0 && done != total)
                throw new IOException($"The download ended early ({done} of {total} bytes).");
        }

        File.Move(partPath, cabinetPath, overwrite: true);
    }

    private void CheckFreeSpace(string root)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            // Wine can report 0 for some mounts; only trust a positive number.
            if (drive.AvailableFreeSpace > 0 && drive.AvailableFreeSpace < RequiredFreeBytes)
            {
                throw new IOException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Not enough free disk space: {drive.AvailableFreeSpace / (1024 * 1024)} MB free, about {RequiredFreeBytes / (1024 * 1024)} MB needed."));
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Could not check free disk space: {ex.Message}");
        }
    }

    /// <summary>
    /// Wine's msedgewebview2.exe never starts in the default Windows 10 mode (Wine bug 58921). This sets the
    /// same per-program Windows 7 mode as "winetricks webview2", and keeps a choice the user already made.
    /// </summary>
    private void PrepareWine()
    {
        if (!isWine)
            return;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Wine\AppDefaults\msedgewebview2.exe");
            if (key.GetValue("Version") is string existing && !string.IsNullOrWhiteSpace(existing))
            {
                log($"Wine already runs msedgewebview2.exe as {existing}.");
                return;
            }

            key.SetValue("Version", "win7");
            log("Set Wine's Windows 7 mode for msedgewebview2.exe (Wine bug 58921).");
        }
        catch (Exception ex)
        {
            log($"Could not set Wine's Windows 7 mode for msedgewebview2.exe: {ex.Message}");
        }
    }

    private void RemoveOlderVersions(string root, string keep)
    {
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(folder, keep, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                log($"Could not remove the old WebView2 in {folder}: {ex.Message}");
            }
        }
    }

    private void Report(WebView2DownloadStage stage, string message, long done = 0, long total = 0)
    {
        progress = new WebView2DownloadProgress(stage, message, done, total);
    }

    /// <summary>Moves every file left in <paramref name="folder"/> into the trash folder; returns their size.</summary>
    private long MoveToTrash(string folder)
    {
        long moved = 0;
        var trash = Path.Combine(WebView2FixedRuntime.Root, WebView2FixedRuntime.TrashFolderName);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                Directory.CreateDirectory(trash);
                var length = new FileInfo(file).Length;
                File.Move(file, Path.Combine(trash, $"{Guid.NewGuid():N}-{Path.GetFileName(file)}"));
                moved += length;
            }
            catch (Exception ex)
            {
                log($"Could not move {file} out of the way: {ex.Message}");
            }
        }

        return moved;
    }

    private static void TryDelete(string? path, bool isFolder)
    {
        if (path == null)
            return;

        try
        {
            if (isFolder && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (!isFolder && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Leftovers are cleaned up on the next download.
        }
    }
}
