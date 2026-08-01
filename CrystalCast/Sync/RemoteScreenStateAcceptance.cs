namespace CrystalCast.Sync;

internal enum RemoteScreenStateDecision
{
    Reject,
    IgnoreSelf,
    IgnoreStale,
    Accept,
}

internal static class RemoteScreenStateAcceptance
{
    public static RemoteScreenStateDecision Evaluate(
        ScreenStateEnvelope? candidate,
        string localOwnerSessionId,
        ScreenStateEnvelope? existing)
    {
        if (candidate == null || candidate.SchemaVersion != 1 || string.IsNullOrWhiteSpace(candidate.ScreenId))
            return RemoteScreenStateDecision.Reject;

        if (candidate.OwnerSessionId == localOwnerSessionId)
            return RemoteScreenStateDecision.IgnoreSelf;

        if (existing != null && existing.Sequence >= candidate.Sequence)
            return RemoteScreenStateDecision.IgnoreStale;

        return RemoteScreenStateDecision.Accept;
    }
}
