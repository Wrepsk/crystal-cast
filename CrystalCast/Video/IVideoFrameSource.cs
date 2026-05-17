namespace CrystalCast.Video;

public interface IVideoFrameSource : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    float FramesPerSecond { get; }
    bool IsRunning { get; }
    string Status { get; }

    void Start();
    void Stop();
    bool TryGetLatestFrame(out VideoFrame frame);
}
