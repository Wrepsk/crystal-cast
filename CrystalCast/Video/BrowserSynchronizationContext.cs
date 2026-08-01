using System.Collections.Concurrent;

namespace CrystalCast.Video;

internal sealed class BrowserSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly AutoResetEvent workAvailable = new(false);
    private bool disposed;

    public WaitHandle WorkAvailable => workAvailable;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (disposed)
            return;

        queue.Enqueue((callback, state));
        try
        {
            workAvailable.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void ExecutePending()
    {
        while (queue.TryDequeue(out var work))
            work.Callback(work.State);
    }

    public void Dispose()
    {
        disposed = true;
        workAvailable.Dispose();
    }
}
