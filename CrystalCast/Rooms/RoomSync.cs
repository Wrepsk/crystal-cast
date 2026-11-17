using System.Globalization;
using CrystalCast.Ipc;

namespace CrystalCast.Rooms;

/// <summary>
/// Decides when a shared screen's state goes to the relay: only when something a viewer would notice changes
/// (link, play/pause, rate, a seek, the screen moving), or when the host's own player has drifted from what it
/// last told the room, for example after it buffered. Nothing is sent while playback just runs: viewers work
/// out the position from the last state, so a quiet room costs no relay requests.
/// </summary>
internal sealed class RoomHostSendPolicy
{
    public const long SeekToleranceMs = 2_000;

    private string lastFingerprint = string.Empty;
    private long lastSentAtMs;
    private long lastPositionMs;
    private bool lastPaused = true;
    private float lastRate = 1.0f;

    /// <summary>
    /// Whether to send <paramref name="state"/> now. While the host's player is <paramref name="buffering"/>,
    /// its position standing still is not sent as a seek; the correction goes out once, when it plays again.
    /// </summary>
    public bool Decide(RoomScreenState state, long nowMs, bool buffering = false)
    {
        var fingerprint = Fingerprint(state);
        var expected = lastPositionMs + (lastPaused ? 0 : (long)((nowMs - lastSentAtMs) * Math.Max(0f, lastRate)));
        var seeked = lastSentAtMs != 0 && !buffering && Math.Abs(state.PositionMs - expected) > SeekToleranceMs;
        if (fingerprint == lastFingerprint && !seeked)
            return false;

        lastFingerprint = fingerprint;
        lastSentAtMs = nowMs;
        lastPositionMs = state.PositionMs;
        lastPaused = state.Paused;
        lastRate = state.Rate;
        return true;
    }

    /// <summary>Everything except the playback position, which moves on its own while playing.</summary>
    internal static string Fingerprint(RoomScreenState state) => string.Join(
        '|',
        state.Name,
        state.Location,
        F(state.PositionX), F(state.PositionY), F(state.PositionZ),
        F(state.YawRadians), F(state.PitchRadians), F(state.RollRadians),
        F(state.WidthMeters), F(state.HeightMeters),
        F(state.Visual.OccludedAlpha), F(state.Visual.OcclusionTolerance), F(state.Visual.ScreenCurveAmountMeters),
        state.Visual.DistanceFadeEnabled, F(state.Visual.FadeStartMeters), F(state.Visual.FadeStopMeters),
        state.Provider, state.Url, state.Paused, F(state.Rate), state.Loop, state.PlaylistAutoplayNext,
        RoomPlaylistSync.Fingerprint(state.Playlist));

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Tells when a player says it is playing but its position stopped moving: it is buffering. Works from the
/// position reports every player page sends, so it needs nothing from the site itself.
/// </summary>
internal sealed class PlaybackStallTracker
{
    /// <summary>How long the position may stand still while playing before it counts as buffering.</summary>
    public const long StallMs = 1_500;

    /// <summary>Less movement than this between reports is standing still.</summary>
    private const long MinimumAdvanceMs = 100;

    private long anchorPositionMs = -1;
    private long anchorAtMs;

    public bool Buffering { get; private set; }

    /// <param name="playing">Whether the player says it is playing.</param>
    /// <param name="positionMs">The position the player last reported, not extrapolated.</param>
    /// <param name="reportedAtMs">When the player reported it.</param>
    public bool Update(bool playing, long positionMs, long reportedAtMs)
    {
        if (!playing || reportedAtMs <= 0)
        {
            anchorPositionMs = -1;
            Buffering = false;
            return false;
        }

        if (anchorPositionMs < 0 || Math.Abs(positionMs - anchorPositionMs) >= MinimumAdvanceMs || reportedAtMs < anchorAtMs)
        {
            anchorPositionMs = positionMs;
            anchorAtMs = reportedAtMs;
            Buffering = false;
            return false;
        }

        Buffering = reportedAtMs - anchorAtMs >= StallMs;
        return Buffering;
    }
}

/// <summary>
/// The relay's clock as seen from this PC. Ping round trips give the offset; the fastest one seen is the
/// most accurate, so it wins.
/// </summary>
internal sealed class RoomClock
{
    private long offsetMs;
    private long bestRoundTripMs = long.MaxValue;

    public bool IsSynced => bestRoundTripMs != long.MaxValue;

    public void AddPong(long sentAtLocalMs, long relayTimeMs, long receivedAtLocalMs)
    {
        var roundTrip = Math.Max(0, receivedAtLocalMs - sentAtLocalMs);
        if (roundTrip > bestRoundTripMs)
            return;

        bestRoundTripMs = roundTrip;
        offsetMs = relayTimeMs - (sentAtLocalMs + (roundTrip / 2));
    }

    /// <summary>Rough offset from a message's relay time before any ping has come back.</summary>
    public void AddRelayTime(long relayTimeMs, long receivedAtLocalMs)
    {
        if (!IsSynced && relayTimeMs > 0)
            offsetMs = relayTimeMs - receivedAtLocalMs;
    }

    public long RelayNow(long localNowMs) => localNowMs + offsetMs;
}

/// <summary>
/// How long a room's host has been offline. A host whose game crashed or lost its connection can't close the
/// room, and can't take it back either, since the host key only lives for that game session. Viewers leave
/// once the host has been gone for <see cref="LeaveAfterMs"/>; a short drop reconnects well before that.
/// </summary>
internal sealed class RoomHostPresence
{
    public const long LeaveAfterMs = 3 * 60 * 1000;

    private long offlineSinceMs = -1;

    /// <summary>Call while this viewer is connected, with what the relay last said about the host.</summary>
    public void Observe(bool hostConnected, long nowMs)
    {
        if (hostConnected)
            offlineSinceMs = -1;
        else if (offlineSinceMs < 0)
            offlineSinceMs = nowMs;
    }

    /// <summary>This viewer lost its own connection, so the host's state is unknown; start over when back.</summary>
    public void Reset() => offlineSinceMs = -1;

    public bool ShouldLeave(long nowMs) => offlineSinceMs >= 0 && nowMs - offlineSinceMs >= LeaveAfterMs;
}

/// <summary>What a viewer's copy of the screen last had applied, so unchanged things aren't sent again.</summary>
internal sealed class RoomViewerApplied
{
    public string Url { get; set; } = string.Empty;
    public bool? Paused { get; set; }
    public long LastSeekAtMs { get; set; }

    /// <summary>Drift seeks in a row, each soon after the last; every one doubles the wait before the next.</summary>
    public int SeekStreak { get; set; }
}

/// <summary>Turns the host's state into an update for the viewer's copy of the screen.</summary>
internal static class RoomViewerPlan
{
    public const string OwnerId = "CrystalCast.Rooms";
    public const long SeekToleranceMs = 2_000;

    /// <summary>After a seek the player needs time to load before its position means anything.</summary>
    public const long SeekCooldownMs = 5_000;

    /// <summary>Longest wait between drift seeks, for a viewer whose connection can't keep up.</summary>
    public const long MaxSeekCooldownMs = 60_000;

    /// <summary>A drift seek this long after the last one starts a new streak.</summary>
    public const long StreakResetMs = 60_000;

    /// <summary>
    /// How long to wait after a drift seek before the next. A viewer on a slow connection buffers after every
    /// seek and falls behind again; seeking every few seconds would keep it buffering forever.
    /// </summary>
    public static long SeekCooldownFor(int streak)
    {
        var doublings = Math.Clamp(streak - 1, 0, 4);
        return Math.Min(MaxSeekCooldownMs, SeekCooldownMs << doublings);
    }

    /// <summary>Where the host's video is now, given when its position was taken.</summary>
    public static long ExpectedPositionMs(RoomScreenState state, long receivedAtRelayMs, long relayNowMs)
    {
        var elapsed = state.Paused ? 0 : Math.Max(0, relayNowMs - receivedAtRelayMs);
        return Math.Max(0, state.PositionMs + (long)(elapsed * Math.Clamp(state.Rate, 0.0f, 4.0f)));
    }

    /// <summary>
    /// Whether the viewer's player is far enough from the host's to seek, and not still settling after the
    /// last seek. Never while it is <paramref name="buffering"/>: it seeks once, when it plays again.
    /// </summary>
    public static bool HasDrifted(long expectedPositionMs, long? localPositionMs, long localNowMs, RoomViewerApplied applied, bool buffering = false)
    {
        return !buffering
            && localPositionMs is { } local
            && Math.Abs(local - expectedPositionMs) > SeekToleranceMs
            && localNowMs - applied.LastSeekAtMs >= SeekCooldownFor(applied.SeekStreak);
    }

    /// <param name="localPositionMs">The viewer's own playback position now, if its player reports one.</param>
    public static ScreenIpcMutationRequest Build(
        string code,
        RoomScreenState state,
        long receivedAtRelayMs,
        long relayNowMs,
        RoomLocation viewerLocation,
        long? localPositionMs,
        long localNowMs,
        RoomViewerApplied applied,
        bool buffering = false)
    {
        var label = $"Room {RoomCode.Format(code)}";
        return Build(
            new RoomMirrorTarget(RoomCode.ScreenIdFor(code), label, label),
            state, receivedAtRelayMs, relayNowMs, viewerLocation, localPositionMs, localNowMs, applied, buffering);
    }

    /// <summary>Same as the room-code overload, for any copy of a shared screen.</summary>
    public static ScreenIpcMutationRequest Build(
        RoomMirrorTarget target,
        RoomScreenState state,
        long receivedAtRelayMs,
        long relayNowMs,
        RoomLocation viewerLocation,
        long? localPositionMs,
        long localNowMs,
        RoomViewerApplied applied,
        bool buffering = false)
    {
        var expected = ExpectedPositionMs(state, receivedAtRelayMs, relayNowMs);
        var urlChanged = !string.Equals(state.Url, applied.Url, StringComparison.Ordinal);
        var pausedChanged = applied.Paused != state.Paused;
        var drifted = HasDrifted(expected, localPositionMs, localNowMs, applied, buffering);
        var seek = urlChanged ? expected > SeekToleranceMs : pausedChanged || drifted;
        if (urlChanged)
            applied.SeekStreak = 0;
        else if (drifted)
            applied.SeekStreak = localNowMs - applied.LastSeekAtMs < StreakResetMs ? applied.SeekStreak + 1 : 1;

        var source = new ScreenSourcePatchDto
        {
            PlaybackPaused = state.Paused,
            PlaybackRate = state.Rate,
            Loop = state.Loop,
            PlaylistAutoplayNext = state.PlaylistAutoplayNext,
        };
        if (urlChanged)
        {
            source.Url = state.Url;
            source.Autoplay = !state.Paused;
            applied.Url = state.Url;
        }

        if (seek)
        {
            source.PositionMs = expected;
            applied.LastSeekAtMs = localNowMs;
        }

        applied.Paused = state.Paused;
        return new ScreenIpcMutationRequest
        {
            ScreenId = target.ScreenId,
            Name = string.IsNullOrWhiteSpace(state.Name) ? target.FallbackName : target.DecorateName(state.Name),
            OwnerId = OwnerId,
            CreateIfMissing = true,
            Enabled = target.Visible && viewerLocation.SamePlaceAs(state.Location),
            Provider = state.Provider,
            SourceControlsLocked = true,
            SourceControlsOwnerId = target.ControlsOwner,
            Placement = new ScreenPlacementPatchDto
            {
                Mode = ScreenPlacementMode.World,
                PositionX = state.PositionX,
                PositionY = state.PositionY,
                PositionZ = state.PositionZ,
                YawRadians = state.YawRadians,
                PitchRadians = state.PitchRadians,
                RollRadians = state.RollRadians,
                WidthMeters = state.WidthMeters,
                HeightMeters = state.HeightMeters,
                ScreenCurveAmountMeters = state.Visual.ScreenCurveAmountMeters,
                OccludedAlpha = state.Visual.OccludedAlpha,
                OcclusionTolerance = state.Visual.OcclusionTolerance,
                DistanceFadeEnabled = state.Visual.DistanceFadeEnabled,
                FadeStartMeters = state.Visual.FadeStartMeters,
                FadeStopMeters = state.Visual.FadeStopMeters,
            },
            Source = source,
        };
    }
}

/// <summary>Which local screen shows a copy of someone's shared screen, and how it is labelled.</summary>
/// <param name="ScreenId">The local copy's screen id; the same shared screen always maps to the same one.</param>
/// <param name="FallbackName">The name when the sharer's screen has none.</param>
/// <param name="ControlsOwner">Shown where the screen's controls are locked, as who controls it.</param>
/// <param name="NameSuffix">Added after the sharer's screen name, such as who shares it.</param>
/// <param name="Visible">False keeps the copy hidden wherever the viewer is.</param>
internal readonly record struct RoomMirrorTarget(
    string ScreenId,
    string FallbackName,
    string ControlsOwner,
    string NameSuffix = "",
    bool Visible = true)
{
    public string DecorateName(string name) => NameSuffix.Length == 0 ? name : $"{name} {NameSuffix}";
}

/// <summary>
/// When a connection pings the relay to learn its clock. A few quick pings right after connecting find a
/// fast round trip; after that the clock barely drifts, so one every few minutes is plenty. Each ping is a
/// relay request, and a room that only plays costs nothing else.
/// </summary>
internal static class RoomPingSchedule
{
    public const int StartupPings = 3;
    public const long StartupIntervalMs = 2_000;
    public const long SteadyIntervalMs = 10 * 60 * 1000;

    public static bool Due(int pingsSent, long lastPingAtMs, long nowMs)
    {
        if (pingsSent == 0)
            return true;

        var interval = pingsSent < StartupPings ? StartupIntervalMs : SteadyIntervalMs;
        return nowMs - lastPingAtMs >= interval;
    }
}
