namespace CrystalCast.Sync;

/// <summary>
/// IPC screen ids the user deleted and asked to keep hidden. Other plugins cannot create them again until the
/// user clears the list or the plugin reloads; nothing is saved.
/// </summary>
internal sealed class HiddenIpcScreens
{
    /// <summary>Keeps a misbehaving plugin cycling ids from growing the list without bound.</summary>
    internal const int MaxEntries = 512;

    private readonly HashSet<string> screenIds = new(StringComparer.Ordinal);

    public int Count => screenIds.Count;

    public void Hide(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId) || screenIds.Count >= MaxEntries)
            return;

        screenIds.Add(screenId);
    }

    public bool IsHidden(string? screenId)
    {
        return !string.IsNullOrWhiteSpace(screenId) && screenIds.Contains(screenId);
    }

    public void Clear() => screenIds.Clear();
}
