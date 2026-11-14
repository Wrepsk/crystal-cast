namespace CrystalCast.Video;

/// <summary>Where a screen's video was when its browser was let go, so a new browser can carry on from there.</summary>
internal sealed record PlaybackResumePoint(string Url, string VideoId, long PositionMs);

/// <summary>
/// Decides when to remember a screen's playback position and when a reloaded player may be sent back to it.
/// Only the video that was loaded is remembered: another link, or another video inside a playlist link, starts
/// normally. Live streams have no duration and are never remembered, so they reload at the live edge.
/// </summary>
internal static class PlaybackResumePolicy
{
    /// <summary>Positions closer to the start than this aren't worth a seek.</summary>
    public const long MinimumPositionMs = 5_000;

    /// <summary>Positions this close to the end would only replay the last moment, so they start over instead.</summary>
    public const long EndMarginMs = 5_000;

    /// <summary>How long a reloaded player may take to report the video before the resume is dropped.</summary>
    public const long ReadyTimeoutMs = 60_000;

    /// <summary>
    /// Whether telemetry describes a video whose player has finished loading, so it says something definite about
    /// the position (a position of 0 while loading would otherwise erase what was remembered).
    /// </summary>
    public static bool IsSettled(MediaPlaybackTelemetry telemetry) =>
        telemetry.Ended || telemetry.IsLive || telemetry.DurationMs > 0;

    /// <summary>The point to resume from later, or null when this playback shouldn't be resumed.</summary>
    public static PlaybackResumePoint? Capture(string url, MediaPlaybackTelemetry telemetry)
    {
        if (string.IsNullOrWhiteSpace(url) || telemetry.Ended || telemetry.IsLive || telemetry.DurationMs <= 0)
            return null;

        if (telemetry.PositionMs < MinimumPositionMs || telemetry.DurationMs - telemetry.PositionMs < EndMarginMs)
            return null;

        return new PlaybackResumePoint(url, telemetry.VideoId ?? string.Empty, telemetry.PositionMs);
    }

    /// <summary>Whether the screen still shows the link the point was taken from.</summary>
    public static bool SameLink(PlaybackResumePoint point, string url) =>
        string.Equals(point.Url, url, StringComparison.Ordinal);

    /// <summary>
    /// Whether a reloaded player that has loaded shows the same video as the point. An empty video id on either
    /// side (a provider that doesn't report one) counts as the same, since the link already matched.
    /// </summary>
    public static bool SameVideo(PlaybackResumePoint point, MediaPlaybackTelemetry telemetry) =>
        string.IsNullOrEmpty(point.VideoId)
        || string.IsNullOrEmpty(telemetry.VideoId)
        || string.Equals(point.VideoId, telemetry.VideoId, StringComparison.Ordinal);
}
