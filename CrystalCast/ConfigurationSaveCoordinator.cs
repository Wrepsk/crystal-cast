using System.Diagnostics;

namespace CrystalCast;

internal sealed class ConfigurationSaveCoordinator(Action persist, TimeSpan debounce)
{
    private long lastRequestTicks;
    private int pending;

    public void Request()
    {
        Interlocked.Exchange(ref lastRequestTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref pending, 1);
    }

    public void Process()
    {
        if (Volatile.Read(ref pending) == 0)
            return;

        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRequestTicks)) < debounce)
            return;

        Flush();
    }

    public void Flush()
    {
        if (Interlocked.Exchange(ref pending, 0) != 0)
            persist();
    }
}
