namespace CrystalRenderer.D3D11;

// Access is serialized by the render pipeline. A monotonic clock keeps timeouts
// independent of time zones and system-clock corrections.
internal sealed class DeferredFrameTracker(Func<long>? clock = null)
{
    private readonly Func<long> clock = clock ?? (() => Environment.TickCount64);
    private long scheduledAt;
    public bool Pending { get; private set; }
    public bool Flushing { get; private set; }
    public bool HasWork => Pending || Flushing;
    public long PendingMilliseconds => HasWork ? Math.Max(0, clock() - scheduledAt) : 0;
    public long ScheduledCount { get; private set; }
    public long FlushedCount { get; private set; }

    public void Schedule()
    {
        if (HasWork) throw new InvalidOperationException("A composite frame is already pending.");
        scheduledAt = clock();
        Pending = true;
        ScheduledCount++;
    }

    public bool TryBeginFlush()
    {
        if (!Pending || Flushing) return false;
        Flushing = true;
        Pending = false;
        return true;
    }

    public void EndFlush(bool success)
    {
        Flushing = false;
        if (success) FlushedCount++;
    }

    public bool Cancel()
    {
        if (!Pending) return false;
        Pending = false;
        return true;
    }

    public bool IsStalled(TimeSpan timeout) => Pending && PendingMilliseconds >= timeout.TotalMilliseconds;
}
