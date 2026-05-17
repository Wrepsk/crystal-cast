using System.Diagnostics;

namespace CrystalCast.Video;

public sealed class GeneratedFrameSource : IVideoFrameSource
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private long sequence;
    private TimeSpan lastFrameAt = TimeSpan.MinValue;

    public GeneratedFrameSource(int width, int height, float fps)
    {
        Width = Math.Clamp(width, 64, 3840);
        Height = Math.Clamp(height, 64, 2160);
        FramesPerSecond = Math.Clamp(fps, 1.0f, 120.0f);
    }

    public string Name => "Generated test frames";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; }
    public bool IsRunning { get; private set; }
    public string Status => IsRunning ? "running" : "stopped";

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void Dispose() => Stop();

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        frame = null!;
        if (!IsRunning)
            return false;

        var now = stopwatch.Elapsed;
        var interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
        if (lastFrameAt != TimeSpan.MinValue && now - lastFrameAt < interval)
            return false;

        lastFrameAt = now;
        var pixels = new byte[Width * Height * 4];
        var t = (float)now.TotalSeconds;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = ((y * Width) + x) * 4;
                var stripe = ((x / 24) + (int)(t * 6)) & 1;
                pixels[offset + 0] = (byte)(stripe == 0 ? 220 : 32);
                pixels[offset + 1] = (byte)((y * 255 / Math.Max(1, Height - 1) + (int)(t * 40)) & 0xFF);
                pixels[offset + 2] = (byte)((x * 255 / Math.Max(1, Width - 1) + (int)(t * 70)) & 0xFF);
                pixels[offset + 3] = 255;
            }
        }

        DrawMovingMarker(pixels, t);
        frame = new VideoFrame(pixels, Width, Height, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return true;
    }

    private void DrawMovingMarker(byte[] pixels, float t)
    {
        var cx = (int)((MathF.Sin(t * 1.7f) * 0.5f + 0.5f) * (Width - 1));
        var cy = (int)((MathF.Cos(t * 1.3f) * 0.5f + 0.5f) * (Height - 1));
        var radius = Math.Max(8, Math.Min(Width, Height) / 14);
        var r2 = radius * radius;

        for (var y = Math.Max(0, cy - radius); y < Math.Min(Height, cy + radius); y++)
        {
            for (var x = Math.Max(0, cx - radius); x < Math.Min(Width, cx + radius); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy > r2)
                    continue;

                var offset = ((y * Width) + x) * 4;
                pixels[offset + 0] = 30;
                pixels[offset + 1] = 245;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }
    }
}
