namespace CrystalCast.Video;

internal enum BrowserLifecycleState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted,
}

internal sealed class BrowserLifecycle
{
    private readonly object gate = new();
    private BrowserLifecycleState state = BrowserLifecycleState.Created;

    public BrowserLifecycleState State
    {
        get
        {
            lock (gate)
                return state;
        }
    }

    public bool CanAcceptCommands
    {
        get
        {
            lock (gate)
                return state is BrowserLifecycleState.Starting or BrowserLifecycleState.Running;
        }
    }

    public bool TryStart()
    {
        lock (gate)
        {
            if (state != BrowserLifecycleState.Created)
                return false;

            state = BrowserLifecycleState.Starting;
            return true;
        }
    }

    public bool TryMarkRunning()
    {
        lock (gate)
        {
            if (state != BrowserLifecycleState.Starting)
                return false;

            state = BrowserLifecycleState.Running;
            return true;
        }
    }

    public bool TryBeginStopping()
    {
        lock (gate)
        {
            if (state is BrowserLifecycleState.Stopping or BrowserLifecycleState.Stopped)
                return false;

            state = BrowserLifecycleState.Stopping;
            return true;
        }
    }

    public void MarkStopped()
    {
        lock (gate)
            state = BrowserLifecycleState.Stopped;
    }

    public void MarkFaulted()
    {
        lock (gate)
        {
            if (state is not (BrowserLifecycleState.Stopping or BrowserLifecycleState.Stopped))
                state = BrowserLifecycleState.Faulted;
        }
    }
}
