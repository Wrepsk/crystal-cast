namespace CrystalCast.Video;

public sealed class NativeVideoFrame
{
    public NativeVideoFrame(IntPtr sharedHandle, int width, int height, long sequence, long timestampUnixMs)
    {
        SharedHandle = sharedHandle;
        Width = width;
        Height = height;
        Sequence = sequence;
        TimestampUnixMs = timestampUnixMs;
    }

    public IntPtr SharedHandle { get; }
    public int Width { get; }
    public int Height { get; }
    public long Sequence { get; }
    public long TimestampUnixMs { get; }
}

public interface INativeVideoFrameSource
{
    bool TryGetLatestNativeFrame(out NativeVideoFrame frame);
}

internal interface INativeVideoFrameAcknowledgement
{
    void AcknowledgeNativeFrame(IntPtr sharedHandle);
}
