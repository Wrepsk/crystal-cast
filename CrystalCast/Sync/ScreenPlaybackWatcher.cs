namespace CrystalCast.Sync;

/// <summary>
/// Notices playback and source changes that happen inside the player itself: pausing in browser controls,
/// a video ending, the next playlist item, a title loading, or a seek. Normal playback progress is not a
/// change; a seek is a position more than <see cref="SeekToleranceMs"/> away from where playback should be.
/// </summary>
internal sealed class ScreenPlaybackWatcher
{
    internal const long SeekToleranceMs = 2000;

    private readonly Dictionary<string, Observation> observations = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the screen's current state and returns what changed since the last call for it. The first call
    /// for a screen only records it.
    /// </summary>
    public IReadOnlyList<ScreenIpcChangeKind> Observe(ScreenStateEnvelope state, long nowUnixMs)
    {
        var playback = state.Playback;
        var next = new Observation(
            playback.State,
            playback.PositionMs,
            playback.DurationMs,
            playback.Rate,
            playback.Loop,
            playback.PlaylistAutoplayNext,
            string.Join('|', state.Source.Provider, state.Source.Identity, state.Source.Url, state.Source.VideoId, state.Source.Title),
            playback.HostTimestampUnixMs > 0 ? playback.HostTimestampUnixMs : nowUnixMs);

        if (!observations.TryGetValue(state.ScreenId, out var previous))
        {
            observations[state.ScreenId] = next;
            return [];
        }

        observations[state.ScreenId] = next;
        var changes = new List<ScreenIpcChangeKind>(2);
        if (!string.Equals(previous.Source, next.Source, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Source);

        if (previous.State != next.State
            || previous.DurationMs != next.DurationMs
            || MathF.Abs(previous.Rate - next.Rate) > 0.001f
            || previous.Loop != next.Loop
            || previous.PlaylistAutoplayNext != next.PlaylistAutoplayNext
            || IsSeek(previous, next))
        {
            changes.Add(ScreenIpcChangeKind.Playback);
        }

        return changes;
    }

    /// <summary>Forgets screens that are gone, so a returning screen starts fresh.</summary>
    public void Retain(IReadOnlySet<string> screenIds)
    {
        foreach (var screenId in observations.Keys.Where(id => !screenIds.Contains(id)).ToList())
            observations.Remove(screenId);
    }

    public void Clear() => observations.Clear();

    private static bool IsSeek(Observation previous, Observation next)
    {
        var expected = previous.PositionMs;
        if (previous.State == ScreenPlaybackState.Playing)
            expected += (long)((next.TimestampUnixMs - previous.TimestampUnixMs) * Math.Max(0f, previous.Rate));

        return Math.Abs(next.PositionMs - expected) > SeekToleranceMs;
    }

    private readonly record struct Observation(
        ScreenPlaybackState State,
        long PositionMs,
        long DurationMs,
        float Rate,
        bool Loop,
        bool PlaylistAutoplayNext,
        string Source,
        long TimestampUnixMs);
}
