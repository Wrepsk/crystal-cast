using Dalamud.Plugin.Ipc;

namespace CrystalCast.Sync;

internal sealed class ScreenChangePublisher
{
    private readonly Configuration configuration;
    private readonly ICallGateProvider<string, object> screenChangedProvider;
    private readonly Dictionary<string, ScreenChangeFingerprint> localScreenFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownLocalScreenIds = new(StringComparer.Ordinal);

    public ScreenChangePublisher(Configuration configuration, ICallGateProvider<string, object> screenChangedProvider)
    {
        this.configuration = configuration;
        this.screenChangedProvider = screenChangedProvider;
    }

    public void Clear()
    {
        localScreenFingerprints.Clear();
        knownLocalScreenIds.Clear();
    }

    public void Remove(string screenId)
    {
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
            var screen = FindBrowserScreen(screenId);
            SendScreenUnavailable(screenId, screen);
            localScreenFingerprints.Remove(screenId);
            knownLocalScreenIds.Remove(screenId);
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
        var changes = new List<ScreenIpcChangeKind>();
        if (request.Placement != null)
            changes.Add(ScreenIpcChangeKind.Placement);
        if (request.SourceControlsLocked.HasValue || !string.IsNullOrWhiteSpace(request.SourceControlsOwnerId))
            changes.Add(ScreenIpcChangeKind.SourceLock);
        AddBrowserChangeKinds(changes, request.Provider.HasValue, request.YouTube, request.Twitch);

        return changes.Count == 0 ? [ScreenIpcChangeKind.Source] : changes.Distinct().ToArray();
    }

    public static ScreenIpcChangeKind[] GetSourceUpdateChangeKinds(ScreenIpcSourceUpdateRequest request)
    {
        var changes = new List<ScreenIpcChangeKind>();
        AddBrowserChangeKinds(changes, request.Provider.HasValue, request.YouTube, request.Twitch);
        return changes.Count == 0 ? [ScreenIpcChangeKind.Source] : changes.Distinct().ToArray();
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
            OwnerSessionId = configuration.OwnerSessionId,
            CreatedByIpc = screen?.CreatedByIpc ?? false,
            OwnerId = screen?.IpcOwnerId ?? string.Empty,
            SourceControlsLocked = screen?.SourceControlsLocked ?? false,
            SourceControlsOwnerId = screen?.SourceControlsOwnerId ?? string.Empty,
            Changes = distinctChanges,
            State = state,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        screenChangedProvider.SendMessage(IpcJsonService.Serialize(evt));
    }

    private void SendScreenUnavailable(string screenId, BrowserScreenProfile? screen)
    {
        var evt = new ScreenIpcChangeEvent
        {
            ScreenId = screenId,
            OwnerSessionId = configuration.OwnerSessionId,
            CreatedByIpc = screen?.CreatedByIpc ?? false,
            OwnerId = screen?.IpcOwnerId ?? string.Empty,
            SourceControlsLocked = screen?.SourceControlsLocked ?? false,
            SourceControlsOwnerId = screen?.SourceControlsOwnerId ?? string.Empty,
            Changes = [ScreenIpcChangeKind.Source],
            State = null,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        screenChangedProvider.SendMessage(IpcJsonService.Serialize(evt));
    }

    private static ScreenChangeFingerprint BuildFingerprint(ScreenStateEnvelope state, BrowserScreenProfile? screen)
    {
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
            Source: string.Join('|',
                state.Source.Kind,
                state.Source.Provider,
                state.Source.Identity,
                state.Source.Title,
                state.Source.Url,
                state.Source.VideoId,
                screen?.YouTubeAutoplay,
                screen?.LoopYouTube,
                screen?.YouTubePlaylistAutoplayNext,
                screen?.YouTubePlaybackRate,
                screen?.YouTubeBrowserWidth,
                screen?.YouTubeBrowserHeight,
                screen?.YouTubeCaptureFps,
                screen?.YouTubeCaptureFpsManual,
                screen?.TwitchUrl,
                screen?.TwitchAutoplay,
                screen?.TwitchBrowserWidth,
                screen?.TwitchBrowserHeight,
                screen?.TwitchCaptureFps,
                screen?.TwitchCaptureFpsManual),
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

    private static void AddBrowserChangeKinds(
        List<ScreenIpcChangeKind> changes,
        bool providerChanged,
        YouTubeScreenPatchDto? youtube,
        TwitchScreenPatchDto? twitch)
    {
        AddYouTubeChangeKinds(changes, providerChanged, youtube);
        AddTwitchChangeKinds(changes, providerChanged, twitch);
    }

    private static void AddYouTubeChangeKinds(List<ScreenIpcChangeKind> changes, bool providerChanged, YouTubeScreenPatchDto? patch)
    {
        if (providerChanged || patch is { Url: not null } || patch?.Autoplay.HasValue == true || patch?.Loop.HasValue == true)
            changes.Add(ScreenIpcChangeKind.Source);
        if (patch?.PlaybackPaused.HasValue == true
            || patch?.PositionMs.HasValue == true
            || patch?.Restart == true
            || patch?.PlaybackRate.HasValue == true
            || patch?.PlaylistAutoplayNext.HasValue == true)
        {
            changes.Add(ScreenIpcChangeKind.Playback);
        }
        if (patch?.BrowserWidth.HasValue == true || patch?.BrowserHeight.HasValue == true || patch?.CaptureFps.HasValue == true || patch?.CaptureFpsManual.HasValue == true)
            changes.Add(ScreenIpcChangeKind.Source);
    }

    private static void AddTwitchChangeKinds(List<ScreenIpcChangeKind> changes, bool providerChanged, TwitchScreenPatchDto? patch)
    {
        if (providerChanged || patch is { Url: not null } || patch?.Autoplay.HasValue == true)
            changes.Add(ScreenIpcChangeKind.Source);
        if (patch?.PlaybackPaused.HasValue == true
            || patch?.PositionMs.HasValue == true
            || patch?.Restart == true)
        {
            changes.Add(ScreenIpcChangeKind.Playback);
        }
        if (patch?.BrowserWidth.HasValue == true || patch?.BrowserHeight.HasValue == true || patch?.CaptureFps.HasValue == true || patch?.CaptureFpsManual.HasValue == true)
            changes.Add(ScreenIpcChangeKind.Source);
    }

    private readonly record struct ScreenChangeFingerprint(
        string Placement,
        string Source,
        string Playback,
        string Visual,
        string Lock);
}
