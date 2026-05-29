using System.Diagnostics;

namespace CrystalCast.Video;

internal sealed class FrameCadenceDiagnostics
{
    private const int MaxSamples = 240;
    private readonly object syncRoot = new();
    private readonly double[] samples = new double[MaxSamples];
    private int sampleCount;
    private int sampleIndex;
    private long lastFrameTicks;
    private long lastSnapshotTicks;
    private string status = "cadence pending";

    public string Status
    {
        get
        {
            lock (syncRoot)
            {
                return status;
            }
        }
    }

    public void Record(float targetFps)
    {
        var now = Stopwatch.GetTimestamp();
        lock (syncRoot)
        {
            if (lastFrameTicks != 0)
            {
                var deltaMs = (now - lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
                if (deltaMs > 0.0 && deltaMs < 2000.0)
                    AddSample(deltaMs);
            }

            lastFrameTicks = now;
            if (lastSnapshotTicks == 0)
            {
                lastSnapshotTicks = now;
                return;
            }

            if (now - lastSnapshotTicks >= Stopwatch.Frequency)
            {
                status = BuildStatus(targetFps);
                lastSnapshotTicks = now;
            }
        }
    }

    public void Reset()
    {
        lock (syncRoot)
        {
            sampleCount = 0;
            sampleIndex = 0;
            lastFrameTicks = 0;
            lastSnapshotTicks = 0;
            status = "cadence pending";
        }
    }

    private void AddSample(double deltaMs)
    {
        samples[sampleIndex] = deltaMs;
        sampleIndex = (sampleIndex + 1) % samples.Length;
        if (sampleCount < samples.Length)
            sampleCount++;
    }

    private string BuildStatus(float targetFps)
    {
        if (sampleCount == 0)
            return "cadence pending";

        var snapshot = new double[sampleCount];
        Array.Copy(samples, snapshot, sampleCount);
        Array.Sort(snapshot);

        var total = 0.0;
        var max = 0.0;
        for (var i = 0; i < snapshot.Length; i++)
        {
            total += snapshot[i];
            max = Math.Max(max, snapshot[i]);
        }

        var average = total / snapshot.Length;
        var p95 = Percentile(snapshot, 0.95);
        var targetMs = 1000.0 / Math.Clamp(targetFps, 1.0f, 120.0f);
        var lateThresholdMs = targetMs * 1.5;
        var lateFrames = 0;
        for (var i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i] > lateThresholdMs)
                lateFrames++;
        }

        return $"cadence {average:0.#}ms avg, p95 {p95:0.#}, max {max:0.#}, late {lateFrames}/{snapshot.Length}";
    }

    private static double Percentile(double[] sortedSamples, double percentile)
    {
        if (sortedSamples.Length == 0)
            return 0.0;

        var index = (int)Math.Ceiling(sortedSamples.Length * percentile) - 1;
        index = Math.Clamp(index, 0, sortedSamples.Length - 1);
        return sortedSamples[index];
    }
}
