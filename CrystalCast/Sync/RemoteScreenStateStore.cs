namespace CrystalCast.Sync;

internal enum RemoteScreenApplyResult
{
    RejectedInvalid,
    RejectedCapacity,
    IgnoredSelf,
    IgnoredDuplicate,
    IgnoredStale,
    Applied,
}

internal readonly record struct RemoteScreenKey(string OwnerSessionId, string ScreenId);

internal sealed class RemoteScreenStateStore
{
    public const int MaxRemoteScreens = 256;
    public const long RemoteScreenTtlMs = 5 * 60 * 1000;

    private readonly Func<long> getNowUnixMs;
    private readonly Dictionary<RemoteScreenKey, Entry> entries = new();

    public RemoteScreenStateStore(Func<long>? getNowUnixMs = null)
    {
        this.getNowUnixMs = getNowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public IReadOnlyCollection<ScreenStateEnvelope> GetSnapshot()
    {
        PruneExpired();
        return entries.Values.Select(entry => entry.State).ToArray();
    }

    public RemoteScreenApplyResult Apply(
        ScreenStateEnvelope? candidate,
        string localOwnerSessionId,
        out string error)
    {
        var nowUnixMs = getNowUnixMs();
        PruneExpired(nowUnixMs);
        if (!ScreenStateValidator.TryValidate(candidate, nowUnixMs, out error))
            return RemoteScreenApplyResult.RejectedInvalid;

        var state = candidate!;
        var key = new RemoteScreenKey(state.OwnerSessionId, state.ScreenId);
        entries.TryGetValue(key, out var existingEntry);
        var decision = RemoteScreenStateAcceptance.Evaluate(state, localOwnerSessionId, existingEntry?.State);
        if (decision == RemoteScreenStateDecision.IgnoreSelf)
        {
            error = string.Empty;
            return RemoteScreenApplyResult.IgnoredSelf;
        }

        if (decision == RemoteScreenStateDecision.IgnoreStale)
        {
            error = string.Empty;
            return RemoteScreenApplyResult.IgnoredStale;
        }

        if (decision == RemoteScreenStateDecision.IgnoreDuplicate)
        {
            error = string.Empty;
            return RemoteScreenApplyResult.IgnoredDuplicate;
        }

        if (decision != RemoteScreenStateDecision.Accept)
            return RemoteScreenApplyResult.RejectedInvalid;

        if (existingEntry == null && entries.Count >= MaxRemoteScreens)
        {
            error = $"Remote screen capacity of {MaxRemoteScreens} has been reached.";
            return RemoteScreenApplyResult.RejectedCapacity;
        }

        entries[key] = new Entry(state, nowUnixMs);
        error = string.Empty;
        return RemoteScreenApplyResult.Applied;
    }

    public bool RemoveByScreenId(string screenId)
    {
        var keys = entries.Keys
            .Where(key => string.Equals(key.ScreenId, screenId, StringComparison.Ordinal))
            .ToArray();
        foreach (var key in keys)
            entries.Remove(key);

        return keys.Length > 0;
    }

    public void Clear() => entries.Clear();

    private void PruneExpired() => PruneExpired(getNowUnixMs());

    private void PruneExpired(long nowUnixMs)
    {
        var expiredKeys = entries
            .Where(pair => nowUnixMs - pair.Value.ReceivedAtUnixMs >= RemoteScreenTtlMs)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expiredKeys)
            entries.Remove(key);
    }

    private sealed record Entry(ScreenStateEnvelope State, long ReceivedAtUnixMs);
}
