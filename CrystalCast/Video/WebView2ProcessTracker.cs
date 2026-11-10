using System.Collections.Concurrent;
using System.Diagnostics;

namespace CrystalCast.Video;

/// <summary>
/// Remembers the WebView2 browser processes CrystalCast starts, so turning the plugin off can end any that
/// a screen failed to close. The start time guards against killing an unrelated process that reused the id.
/// </summary>
internal static class WebView2ProcessTracker
{
    private static readonly ConcurrentDictionary<int, DateTime> Processes = new();

    public static void Track(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            Processes[processId] = process.StartTime;
        }
        catch
        {
            // Already gone, or not readable; nothing to clean up later.
        }
    }

    /// <summary>
    /// Waits up to <paramref name="grace"/> for the tracked browsers to exit by themselves, then ends the rest.
    /// Returns how many had to be ended.
    /// </summary>
    public static int EndRemaining(TimeSpan grace)
    {
        var deadline = DateTime.UtcNow + grace;
        var ended = 0;
        foreach (var (processId, startTime) in Processes.ToArray())
        {
            Processes.TryRemove(processId, out _);
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!IsSameProcess(process, startTime))
                    continue;

                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero && process.WaitForExit(remaining))
                    continue;

                process.Kill(entireProcessTree: true);
                ended++;
            }
            catch
            {
                // Exited in the meantime, or access was refused.
            }
        }

        return ended;
    }

    internal static bool IsSameProcess(Process process, DateTime trackedStartTime)
    {
        return !process.HasExited
            && process.StartTime == trackedStartTime
            && process.ProcessName.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase);
    }
}
