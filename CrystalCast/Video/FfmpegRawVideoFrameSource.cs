using System.Diagnostics;
using System.Globalization;

namespace CrystalCast.Video;

public sealed class FfmpegRawVideoFrameSource : IVideoFrameSource
{
    private readonly string ffmpegPath;
    private readonly string videoPath;
    private readonly bool loop;
    private readonly VideoDimensions sourceDimensions;
    private CancellationTokenSource? cancellation;
    private Process? process;
    private Task? readTask;
    private VideoFrame? latestFrame;
    private long sequence;

    public FfmpegRawVideoFrameSource(string ffmpegPath, string videoPath, float scalePercent, float fps, bool loop, int fallbackWidth, int fallbackHeight)
    {
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg.exe" : ffmpegPath;
        this.videoPath = videoPath;
        if (!FfmpegVideoProbe.TryProbeSize(this.ffmpegPath, videoPath, out sourceDimensions, out var probeMessage))
        {
            sourceDimensions = new VideoDimensions(
                Math.Clamp(fallbackWidth, 64, 3840),
                Math.Clamp(fallbackHeight, 64, 2160));
            Status = $"using fallback size: {probeMessage}";
        }

        var scaledDimensions = FfmpegVideoProbe.Scale(sourceDimensions, scalePercent);
        Width = scaledDimensions.Width;
        Height = scaledDimensions.Height;
        ScalePercent = Math.Clamp(scalePercent, 5.0f, 200.0f);
        FramesPerSecond = Math.Clamp(fps, 1.0f, 120.0f);
        this.loop = loop;
    }

    public string Name => "Local video via ffmpeg";
    public int Width { get; }
    public int Height { get; }
    public float ScalePercent { get; }
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

        var resolvedFfmpegPath = FfmpegLocator.ResolveFfmpegPath(ffmpegPath);
        if (resolvedFfmpegPath == null)
        {
            Status = $"ffmpeg not found: {ffmpegPath}. Set FFmpeg path to the full ffmpeg.exe path, put ffmpeg.exe next to CrystalCast.dll, or install FFmpeg on PATH.";
            return;
        }

        cancellation = new CancellationTokenSource();
        var psi = new ProcessStartInfo
        {
            FileName = resolvedFfmpegPath,
            WorkingDirectory = FfmpegLocator.ResolveWorkingDirectory(videoPath),
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
        psi.ArgumentList.Add($"fps={fpsText},scale=w={Width}:h={Height}:flags=bicubic,setsar=1,format=bgra");
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
            Status = $"running: {sourceDimensions} -> {Width}x{Height} ({ScalePercent:0.#}%)";
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

}
