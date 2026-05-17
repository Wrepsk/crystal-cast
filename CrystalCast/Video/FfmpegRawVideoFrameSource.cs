using System.Diagnostics;
using System.Globalization;

namespace CrystalCast.Video;

public sealed class FfmpegRawVideoFrameSource : IVideoFrameSource
{
    private readonly string ffmpegPath;
    private readonly string videoPath;
    private readonly bool loop;
    private CancellationTokenSource? cancellation;
    private Process? process;
    private Task? readTask;
    private VideoFrame? latestFrame;
    private long sequence;

    public FfmpegRawVideoFrameSource(string ffmpegPath, string videoPath, int width, int height, float fps, bool loop)
    {
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg.exe" : ffmpegPath;
        this.videoPath = videoPath;
        Width = Math.Clamp(width, 64, 3840);
        Height = Math.Clamp(height, 64, 2160);
        FramesPerSecond = Math.Clamp(fps, 1.0f, 120.0f);
        this.loop = loop;
    }

    public string Name => "Local video via ffmpeg";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; }
    public bool IsRunning => process is { HasExited: false };
    public string Status { get; private set; } = "stopped";

    public void Start()
    {
        if (IsRunning)
            return;

        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            Status = "video file not found";
            return;
        }

        var resolvedFfmpegPath = ResolveFfmpegPath(ffmpegPath);
        if (resolvedFfmpegPath == null)
        {
            Status = $"ffmpeg not found: {ffmpegPath}. Set FFmpeg path to the full ffmpeg.exe path, put ffmpeg.exe next to CrystalCast.dll, or install FFmpeg on PATH.";
            return;
        }

        cancellation = new CancellationTokenSource();
        var psi = new ProcessStartInfo
        {
            FileName = resolvedFfmpegPath,
            WorkingDirectory = ResolveWorkingDirectory(videoPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (loop)
        {
            psi.ArgumentList.Add("-stream_loop");
            psi.ArgumentList.Add("-1");
        }

        psi.ArgumentList.Add("-re");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-vf");
        var fpsText = FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture);
        psi.ArgumentList.Add($"scale=w={Width}:h={Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=black,fps={fpsText},format=bgra");
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            process = Process.Start(psi);
            if (process == null)
            {
                Status = "failed to start ffmpeg";
                return;
            }

            _ = Task.Run(async () =>
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error))
                    Status = error.Trim();
            });

            readTask = Task.Run(() => ReadLoop(cancellation.Token));
            Status = $"running: {resolvedFfmpegPath}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public void Stop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;

        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort process cleanup during plugin unload/source changes.
        }

        process?.Dispose();
        process = null;
        readTask = null;
        Status = "stopped";
    }

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        frame = latestFrame!;
        return frame != null;
    }

    public void Dispose() => Stop();

    private async Task ReadLoop(CancellationToken token)
    {
        var currentProcess = process;
        if (currentProcess == null)
            return;

        var frameSize = checked(Width * Height * 4);
        var scratch = new byte[frameSize];
        var stream = currentProcess.StandardOutput.BaseStream;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var offset = 0;
                while (offset < frameSize)
                {
                    var read = await stream.ReadAsync(scratch.AsMemory(offset, frameSize - offset), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        Status = "ffmpeg ended";
                        return;
                    }

                    offset += read;
                }

                var pixels = new byte[frameSize];
                Buffer.BlockCopy(scratch, 0, pixels, 0, frameSize);
                var frame = new VideoFrame(pixels, Width, Height, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Interlocked.Exchange(ref latestFrame, frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private static string? ResolveFfmpegPath(string configuredPath)
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

        var executableName = Path.GetExtension(candidate).Length == 0 ? $"{candidate}.exe" : candidate;
        foreach (var directory in GetSearchDirectories())
        {
            var path = Path.Combine(directory, executableName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static IEnumerable<string> GetSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in GetPreferredSearchDirectories())
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && seen.Add(directory))
                yield return directory;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(directory) && seen.Add(directory))
                yield return directory;
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

    private static string ResolveWorkingDirectory(string inputPath)
    {
        var videoDirectory = Path.GetDirectoryName(inputPath);
        if (!string.IsNullOrWhiteSpace(videoDirectory) && Directory.Exists(videoDirectory))
            return videoDirectory;

        return Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? AppContext.BaseDirectory;
    }
}
