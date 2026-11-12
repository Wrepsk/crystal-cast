namespace CrystalCast.Video;

internal sealed class CaptureFrameRateLimiter
{
    private long lastPublishedTicks;

    public bool ShouldPublish(float requestedFps, long nowTicks, long timestampFrequency)
    {
        var captureFps = Math.Clamp(requestedFps, 1.0f, 120.0f);
        if (captureFps >= 55.0f)
            return true;

        var previous = Interlocked.Read(ref lastPublishedTicks);
        var minimumTicks = (long)(timestampFrequency / captureFps * 0.85);
        if (previous != 0 && nowTicks - previous < minimumTicks)
            return false;

        Interlocked.Exchange(ref lastPublishedTicks, nowTicks);
        return true;
    }
}
