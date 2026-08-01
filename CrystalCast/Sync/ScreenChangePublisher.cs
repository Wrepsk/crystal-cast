using Dalamud.Plugin.Ipc;

namespace CrystalCast.Sync;

internal sealed class ScreenChangePublisher
{
    private readonly Configuration configuration;
    private readonly Action<string> sendMessage;
    private readonly string ownerSessionId;
    private readonly Dictionary<string, ScreenChangeFingerprint> localScreenFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownLocalScreenIds = new(StringComparer.Ordinal);

    public ScreenChangePublisher(
        Configuration configuration,
        ICallGateProvider<string, object> screenChangedProvider,
        string ownerSessionId)
    {
        this.configuration = configuration;
        sendMessage = screenChangedProvider.SendMessage;
        this.ownerSessionId = ownerSessionId;
    }

    internal ScreenChangePublisher(
        Configuration configuration,
        string ownerSessionId,
        Action<string> sendMessage)
    {
        this.configuration = configuration;
        this.ownerSessionId = ownerSessionId;
        this.sendMessage = sendMessage;
    }

    public void Clear()
    {
        localScreenFingerprints.Clear();
        knownLocalScreenIds.Clear();
    }

    public void SendUnavailableAndForget(string screenId, BrowserScreenProfile? screen = null)
    {
        SendScreenUnavailable(screenId, screen ?? FindBrowserScreen(screenId));
        localScreenFingerprints.Remove(screenId);
        knownLocalScreenIds.Remove(screenId);
    }

    public void MaybeSendScreenChanged(
        ScreenStateEnvelope state,
        string? forcedScreenId,
        IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        var screen = FindBrowserScreen(state.ScreenId);
        var next = BuildFingerprint(state, screen);
        if (!localScreenFingerprints.TryGetValue(state.ScreenId, out var previous))
        {
            localScreenFingerprints[state.ScreenId] = next;
            SendScreenChanged(
                state,
                screen,
                IsForcedScreen(state.ScreenId, forcedScreenId, forcedChanges)
                    ? forcedChanges!
                    : GetCreateChangeKinds());

            return;
        }

        localScreenFingerprints[state.ScreenId] = next;
        if (IsForcedScreen(state.ScreenId, forcedScreenId, forcedChanges))
        {
            SendScreenChanged(state, screen, forcedChanges!);
            return;
        }

        var changes = GetFingerprintChanges(previous, next);
        if (changes.Count > 0)
            SendScreenChanged(state, screen, changes);
    }

    public void SendUnavailableEventsForMissingLocalScreens(HashSet<string> currentScreenIds)
    {
        foreach (var screenId in knownLocalScreenIds.Except(currentScreenIds, StringComparer.Ordinal).ToList())
        {
            SendUnavailableAndForget(screenId);
        }
    }

    public void RememberKnownLocalScreens(IEnumerable<string> screenIds)
    {
        foreach (var screenId in screenIds)
        {
            if (!string.IsNullOrWhiteSpace(screenId))
                knownLocalScreenIds.Add(screenId);
        }
    }

    public static ScreenIpcChangeKind[] GetCreateChangeKinds()
    {
        return
        [
            ScreenIpcChangeKind.Created,
            ScreenIpcChangeKind.Placement,
            ScreenIpcChangeKind.Source,
            ScreenIpcChangeKind.Playback,
        ];
    }

    public static ScreenIpcChangeKind[] GetMutationChangeKinds(ScreenIpcMutationRequest request)
    {
        return BrowserSourceIpcAdapters.GetMutationChangeKinds(request);
    }

    public static ScreenIpcChangeKind[] GetSourceUpdateChangeKinds(ScreenIpcSourceUpdateRequest request)
    {
        return BrowserSourceIpcAdapters.GetSourceUpdateChangeKinds(request);
    }

    private BrowserScreenProfile? FindBrowserScreen(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return configuration.BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }

    private static bool IsForcedScreen(
        string screenId,
        string? forcedScreenId,
        IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        return forcedChanges is { Count: > 0 }
            && string.Equals(screenId, IpcJsonService.NormalizeText(forcedScreenId), StringComparison.Ordinal);
    }

    private void SendScreenChanged(
        ScreenStateEnvelope state,
        BrowserScreenProfile? screen,
        IReadOnlyCollection<ScreenIpcChangeKind> changes)
    {
        var distinctChanges = changes.Distinct().ToArray();
        if (distinctChanges.Length == 0)
            return;

        var evt = new ScreenIpcChangeEvent
        {
            ScreenId = state.ScreenId,
            OwnerSessionId = ownerSessionId,
            CreatedByIpc = screen?.CreatedByIpc ?? false,
            OwnerId = screen?.IpcOwnerId ?? string.Empty,
            SourceControlsLocked = screen?.SourceControlsLocked ?? false,
            SourceControlsOwnerId = screen?.SourceControlsOwnerId ?? string.Empty,
            Changes = distinctChanges,
            State = state,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        sendMessage(IpcJsonService.Serialize(evt));
    }

    private void SendScreenUnavailable(string screenId, BrowserScreenProfile? screen)
    {
        var evt = new ScreenIpcChangeEvent
        {
            ScreenId = screenId,
            OwnerSessionId = ownerSessionId,
            CreatedByIpc = screen?.CreatedByIpc ?? false,
            OwnerId = screen?.IpcOwnerId ?? string.Empty,
            SourceControlsLocked = screen?.SourceControlsLocked ?? false,
            SourceControlsOwnerId = screen?.SourceControlsOwnerId ?? string.Empty,
            Changes = [ScreenIpcChangeKind.Unavailable],
            State = null,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        sendMessage(IpcJsonService.Serialize(evt));
    }

    private static ScreenChangeFingerprint BuildFingerprint(ScreenStateEnvelope state, BrowserScreenProfile? screen)
    {
        var sourceParts = new List<object?>
        {
            state.Source.Kind,
            state.Source.Provider,
            state.Source.Identity,
            state.Source.Title,
            state.Source.Url,
            state.Source.VideoId,
        };
        BrowserSourceIpcAdapters.AddFingerprintParts(screen, sourceParts);

        return new ScreenChangeFingerprint(
            Placement: string.Join('|',
                state.Position.X,
                state.Position.Y,
                state.Position.Z,
                state.Rotation.X,
                state.Rotation.Y,
                state.Rotation.Z,
                state.Rotation.W,
                state.SizeMeters.X,
                state.SizeMeters.Y),
            Source: string.Join('|', sourceParts),
            Playback: string.Join('|',
                state.Playback.State,
                state.Playback.PositionMs,
                state.Playback.DurationMs,
                state.Playback.Rate,
                state.Playback.Loop,
                state.Playback.PlaylistAutoplayNext),
            Visual: string.Join('|',
                state.Visual.OccludedAlpha,
                state.Visual.OcclusionTolerance,
                state.Visual.ScreenCurveAmountMeters,
                state.Visual.DistanceFadeEnabled,
                state.Visual.FadeStartMeters,
                state.Visual.FadeStopMeters),
            Lock: string.Join('|',
                screen?.SourceControlsLocked,
                screen?.SourceControlsOwnerId));
    }

    private static List<ScreenIpcChangeKind> GetFingerprintChanges(ScreenChangeFingerprint previous, ScreenChangeFingerprint next)
    {
        var changes = new List<ScreenIpcChangeKind>();
        if (!string.Equals(previous.Placement, next.Placement, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Placement);
        if (!string.Equals(previous.Source, next.Source, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Source);
        if (!string.Equals(previous.Playback, next.Playback, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Playback);
        if (!string.Equals(previous.Visual, next.Visual, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Visual);
        if (!string.Equals(previous.Lock, next.Lock, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.SourceLock);

        return changes;
    }

    private readonly record struct ScreenChangeFingerprint(
        string Placement,
        string Source,
        string Playback,
        string Visual,
        string Lock);
}
