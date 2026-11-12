using Dalamud.Plugin.Services;

namespace CrystalCast.Ipc;

/// <summary>
/// Runs IPC handlers on the game's main thread, where CrystalCast draws and changes its screens. Another
/// plugin may call from any thread (a sync plugin often calls from its network thread); those calls are
/// handed to the main thread and waited for. Calls already on the main thread run directly, so a caller
/// on the main thread can never deadlock waiting for itself.
/// </summary>
internal sealed class IpcMainThreadDispatcher : IDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly IFramework framework;
    private int mainThreadId;

    public IpcMainThreadDispatcher(IFramework framework)
    {
        this.framework = framework;
        if (framework.IsInFrameworkUpdateThread)
            mainThreadId = Environment.CurrentManagedThreadId;
        framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Runs <paramref name="handler"/> on the main thread. <paramref name="onTimeout"/> builds the answer when
    /// the main thread does not get to it in time, for example while the game is loading.
    /// </summary>
    public T Invoke<T>(Func<T> handler, Func<string, T> onTimeout)
    {
        var mainThread = Volatile.Read(ref mainThreadId);
        if (mainThread == 0 || mainThread == Environment.CurrentManagedThreadId || framework.IsInFrameworkUpdateThread)
            return handler();

        var task = framework.RunOnFrameworkThread(handler);
        try
        {
            if (!task.Wait(Timeout))
                return onTimeout($"CrystalCast's game thread did not take the call within {Timeout.TotalSeconds:0} seconds; try again.");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        return task.Result;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework sender)
    {
        if (Volatile.Read(ref mainThreadId) == 0)
            Volatile.Write(ref mainThreadId, Environment.CurrentManagedThreadId);
    }
}
