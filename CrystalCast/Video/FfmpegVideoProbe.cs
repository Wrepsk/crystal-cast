using System.Diagnostics;
using System.Globalization;

namespace CrystalCast.Video;

internal static class FfmpegVideoProbe
{
    public static bool TryProbeSize(string ffmpegPath, string videoPath, out VideoDimensions dimensions, out string message)
    {
        dimensions = default;
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            message = "video file not found";
            return false;
        }

        var ffprobePath = FfmpegLocator.ResolveFfprobePath(ffmpegPath);
        if (ffprobePath == null)
        {
            message = "ffprobe not found";
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            WorkingDirectory = FfmpegLocator.ResolveWorkingDirectory(videoPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=width,height");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0:s=x");
        psi.ArgumentList.Add(videoPath);

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                message = "failed to start ffprobe";
                return false;
            }

            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                message = "ffprobe timed out";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                message = string.IsNullOrWhiteSpace(error) ? $"ffprobe exited with {process.ExitCode}" : error.Trim();
                return false;
            }

            if (TryParseSize(output, out dimensions))
                return true;

            message = "ffprobe did not return video dimensions";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public static VideoDimensions Scale(VideoDimensions source, float scalePercent)
    {
        if (source.Width <= 0 || source.Height <= 0)
            source = new VideoDimensions(512, 288);

        var scale = Math.Clamp(scalePercent, 5.0f, 200.0f) / 100.0f;
        var width = source.Width * scale;
        var height = source.Height * scale;

        var maxScale = Math.Min(3840.0f / width, 2160.0f / height);
        if (maxScale < 1.0f)
        {
            width *= maxScale;
            height *= maxScale;
        }

        var minScale = Math.Max(64.0f / width, 64.0f / height);
        if (minScale > 1.0f)
        {
            width *= minScale;
            height *= minScale;
        }

        return new VideoDimensions(ToEven(width), ToEven(height));
    }

    private static bool TryParseSize(string output, out VideoDimensions dimensions)
    {
        dimensions = default;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('x', StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) &&
                width > 0 &&
                height > 0)
            {
                dimensions = new VideoDimensions(width, height);
                return true;
            }
        }

        return false;
    }

    private static int ToEven(float value)
    {
        var rounded = Math.Max(2, (int)MathF.Round(value));
        return rounded % 2 == 0 ? rounded : rounded + 1;
    }
}

internal readonly record struct VideoDimensions(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}
