using System.Buffers;

namespace CrystalCast.Video;

public sealed class VideoFrame : IDisposable
{
    public VideoFrame(byte[] pixels, int width, int height, long sequence, long timestampUnixMs)
        : this(pixels, width * height * 4, width, height, sequence, timestampUnixMs, pooled: false)
    {
    }

    private VideoFrame(byte[] pixels, int pixelLength, int width, int height, long sequence, long timestampUnixMs, bool pooled)
    {
        Pixels = pixels;
        PixelLength = pixelLength;
        Width = width;
        Height = height;
        Sequence = sequence;
        TimestampUnixMs = timestampUnixMs;
        this.pooled = pooled;
    }

    public byte[] Pixels { get; }
    public int PixelLength { get; }
    public int Width { get; }
    public int Height { get; }
    public long Sequence { get; }
    public long TimestampUnixMs { get; }

    private readonly bool pooled;
    private int referenceCount = 1;

    internal static VideoFrame Rent(int width, int height, long sequence, long timestampUnixMs)
    {
        var length = checked(width * height * 4);
        return new VideoFrame(
            ArrayPool<byte>.Shared.Rent(length),
            length,
            width,
            height,
            sequence,
            timestampUnixMs,
            pooled: true);
    }

    internal bool TryAcquire(out VideoFrame frame)
    {
        if (!pooled)
        {
            frame = this;
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref referenceCount);
            if (current <= 0)
            {
                frame = null!;
                return false;
            }

            if (Interlocked.CompareExchange(ref referenceCount, current + 1, current) == current)
            {
                frame = this;
                return true;
            }
        }
    }

    public void Dispose()
    {
        if (!pooled || Interlocked.Decrement(ref referenceCount) != 0)
            return;

        ArrayPool<byte>.Shared.Return(Pixels);
    }
}
