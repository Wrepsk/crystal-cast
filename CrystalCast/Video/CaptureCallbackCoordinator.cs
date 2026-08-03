namespace CrystalCast.Video;

internal sealed class CaptureCallbackCoordinator
{
    private readonly object processingLock = new();
    private int fatalReported;
    private int stopping;

    public bool IsStopping => Volatile.Read(ref stopping) != 0;
    public bool IsInactive => IsStopping || Volatile.Read(ref fatalReported) != 0;

    public bool TryBeginStop() => Interlocked.Exchange(ref stopping, 1) == 0;

    public bool TryRun(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsInactive)
            return false;

        lock (processingLock)
        {
            if (IsInactive)
                return false;

            action();
            return true;
        }
    }

    public void RunCallback(Action action, Action<Exception> reportFatal)
    {
        ArgumentNullException.ThrowIfNull(reportFatal);
        try
        {
            TryRun(action);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref fatalReported, 1) == 0)
            {
                try
                {
                    reportFatal(ex);
                }
                catch
                {
                    // No managed exception may escape a native FrameArrived callback.
                }
            }
        }
    }

    public void WaitForIdleAndRun(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (processingLock)
            action();
    }
}
