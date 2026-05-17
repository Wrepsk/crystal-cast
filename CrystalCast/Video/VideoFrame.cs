namespace CrystalCast.Video;

public sealed class VideoFrame
{
    public VideoFrame(byte[] pixels, int width, int height, long sequence, long timestampUnixMs)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Sequence = sequence;
        TimestampUnixMs = timestampUnixMs;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public long Sequence { get; }
    public long TimestampUnixMs { get; }
}
