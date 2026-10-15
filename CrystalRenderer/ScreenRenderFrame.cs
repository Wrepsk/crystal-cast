namespace CrystalRenderer;

internal sealed class ScreenRenderFrame(
    int capacity,
    Action<ScreenSurface> submit,
    Action<bool> finish) : IScreenRenderFrame
{
    private int completed;
    private int count;

    public void Submit(ScreenSurface surface)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref completed) != 0, this);
        surface.Validate();
        if (count >= capacity)
            throw new InvalidOperationException($"Screen surface capacity ({capacity}) exceeded.");
        submit(surface);
        count++;
    }

    public void Cancel() => Complete(cancelled: true);
    public void Dispose() => Complete(cancelled: false);

    private void Complete(bool cancelled)
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
            finish(cancelled);
    }
}
