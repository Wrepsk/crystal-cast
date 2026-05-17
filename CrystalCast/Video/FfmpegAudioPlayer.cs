using System.Diagnostics;
using NAudio.Wave;

namespace CrystalCast.Video;

public sealed class FfmpegAudioPlayer : IDisposable
{
    private readonly string ffmpegPath;
    private readonly string videoPath;
    private readonly bool loop;
    private CancellationTokenSource? cancellation;
    private Process? process;
    private Task? readTask;
    private BufferedWaveProvider? buffer;
    private WaveOutEvent? output;

    public FfmpegAudioPlayer(string ffmpegPath, string videoPath, bool loop, float volume)
    {
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg.exe" : ffmpegPath;
        this.videoPath = videoPath;
        this.loop = loop;
        Volume = ClampVolume(volume);
    }

    public bool IsRunning => process is { HasExited: false };
    public float Volume { get; private set; }
    public string Status { get; private set; } = "stopped";

    public void Start()
    {
        if (IsRunning)
            return;

        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            Status = "audio video file not found";
            return;
        }

        var resolvedFfmpegPath = FfmpegLocator.ResolveFfmpegPath(ffmpegPath);
        if (resolvedFfmpegPath == null)
        {
            Status = $"audio ffmpeg not found: {ffmpegPath}";
            return;
        }

        cancellation = new CancellationTokenSource();

        var waveFormat = new WaveFormat(48000, 16, 2);
        buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };

        output = new WaveOutEvent
        {
            DesiredLatency = 180,
            Volume = 1.0f,
        };
        output.Init(buffer);
        output.Play();

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
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            process = Process.Start(psi);
            if (process == null)
            {
                Status = "failed to start audio ffmpeg";
                Stop();
                return;
            }

            _ = Task.Run(async () =>
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error))
                    Status = error.Trim();
            });

            readTask = Task.Run(() => ReadLoop(cancellation.Token));
            Status = $"audio running: {resolvedFfmpegPath}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Stop();
        }
    }

    public void SetVolume(float volume)
    {
        Volume = ClampVolume(volume);
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

        output?.Stop();
        output?.Dispose();
        output = null;
        buffer = null;
        Status = "stopped";
    }

    public void Dispose() => Stop();

    private async Task ReadLoop(CancellationToken token)
    {
        var currentProcess = process;
        var currentBuffer = buffer;
        if (currentProcess == null || currentBuffer == null)
            return;

        var scratch = new byte[16 * 1024];
        var stream = currentProcess.StandardOutput.BaseStream;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), token).ConfigureAwait(false);
                if (read == 0)
                {
                    Status = "audio ended";
                    return;
                }

                ApplyVolumeInPlace(scratch, read, Volume);
                currentBuffer.AddSamples(scratch, 0, read);
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

    private static float ClampVolume(float volume)
    {
        return float.IsFinite(volume) ? Math.Clamp(volume, 0.0f, 1.0f) : 0.0f;
    }

    private static void ApplyVolumeInPlace(byte[] samples, int byteCount, float volume)
    {
        if (volume >= 0.999f)
            return;

        if (volume <= 0.001f)
        {
            Array.Clear(samples, 0, byteCount);
            return;
        }

        var alignedByteCount = byteCount & ~1;
        for (var i = 0; i < alignedByteCount; i += 2)
        {
            var sample = (short)(samples[i] | (samples[i + 1] << 8));
            var scaled = (short)(sample * volume);
            samples[i] = (byte)(scaled & 0xFF);
            samples[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }
}
