namespace CrystalCast.Video;

public sealed class MediaPlaybackTelemetry
{
    public ScreenPlaybackState State { get; init; } = ScreenPlaybackState.Stopped;
    public long PositionMs { get; init; }
    public float Rate { get; init; } = 1.0f;
    public string Title { get; init; } = string.Empty;
    public string VideoId { get; init; } = string.Empty;
    public string CanonicalUrl { get; init; } = string.Empty;
    public long HostTimestampUnixMs { get; init; }
}

public interface IMediaPlaybackTelemetrySource
{
    bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry telemetry);
}

public interface IMediaPlaybackController
{
    void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop);
    void Play();
    void Pause();
    void SeekBy(double seconds);
    void Restart();
}
