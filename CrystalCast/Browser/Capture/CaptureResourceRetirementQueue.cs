namespace CrystalCast.Video;

internal sealed class CaptureResourceRetirementQueue<T> : IDisposable
    where T : IDisposable
{
    private readonly int capacity;
    private readonly List<T> resources = new();

    public CaptureResourceRetirementQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    public int Count => resources.Count;
    public bool CanRetire => resources.Count < capacity;

    public void Retire(T resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!CanRetire)
            throw new InvalidOperationException($"At most {capacity} resources may await acknowledgement.");

        resources.Add(resource);
    }

    public void AcknowledgeCurrent()
    {
        foreach (var resource in resources)
            resource.Dispose();

        resources.Clear();
    }

    public void Dispose() => AcknowledgeCurrent();
}
