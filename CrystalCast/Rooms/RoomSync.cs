using System.Globalization;
using CrystalCast.Ipc;

namespace CrystalCast.Rooms;

/// <summary>
/// Decides when a shared screen's state goes to the relay: right away when something a viewer would notice
/// changes (link, play/pause, rate, a seek, the screen moving), and otherwise as a heartbeat every
/// <see cref="HeartbeatMs"/> so viewers who drifted catch up. Heartbeats are marked as such so the relay
/// doesn't store them.
/// </summary>
internal sealed class RoomHostSendPolicy
{
    public const long HeartbeatMs = 10_000;
    public const long SeekToleranceMs = 2_000;

    private string lastFingerprint = string.Empty;
    private long lastSentAtMs;
    private long lastPositionMs;
    private bool lastPaused = true;
    private float lastRate = 1.0f;

    /// <summary>Returns whether to send now, and whether it is a real change (true) or a heartbeat (false).</summary>
    public (bool Send, bool Change) Decide(RoomScreenState state, long nowMs)
    {
        var fingerprint = Fingerprint(state);
        var expected = lastPositionMs + (lastPaused ? 0 : (long)((nowMs - lastSentAtMs) * Math.Max(0f, lastRate)));
        var seeked = lastSentAtMs != 0 && Math.Abs(state.PositionMs - expected) > SeekToleranceMs;

        bool change;
        if (fingerprint != lastFingerprint || seeked)
            change = true;
        else if (nowMs - lastSentAtMs >= HeartbeatMs)
            change = false;
        else
            return (false, false);

        lastFingerprint = fingerprint;
        lastSentAtMs = nowMs;
        lastPositionMs = state.PositionMs;
        lastPaused = state.Paused;
        lastRate = state.Rate;
        return (true, change);
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
}

/// <summary>Turns the host's state into an update for the viewer's copy of the screen.</summary>
internal static class RoomViewerPlan
{
    public const string OwnerId = "CrystalCast.Rooms";
    public const long SeekToleranceMs = 2_000;

    /// <summary>After a seek the player needs time to load before its position means anything.</summary>
    public const long SeekCooldownMs = 5_000;

    /// <summary>Where the host's video is now, given when its position was taken.</summary>
    public static long ExpectedPositionMs(RoomScreenState state, long receivedAtRelayMs, long relayNowMs)
    {
        var elapsed = state.Paused ? 0 : Math.Max(0, relayNowMs - receivedAtRelayMs);
        return Math.Max(0, state.PositionMs + (long)(elapsed * Math.Clamp(state.Rate, 0.0f, 4.0f)));
    }

    /// <summary>
    /// Whether the viewer's player is far enough from the host's to seek, and not still settling after the
    /// last seek.
    /// </summary>
    public static bool HasDrifted(long expectedPositionMs, long? localPositionMs, long localNowMs, RoomViewerApplied applied)
    {
        return localPositionMs is { } local
            && Math.Abs(local - expectedPositionMs) > SeekToleranceMs
            && localNowMs - applied.LastSeekAtMs >= SeekCooldownMs;
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
        RoomViewerApplied applied)
    {
        var expected = ExpectedPositionMs(state, receivedAtRelayMs, relayNowMs);
        var urlChanged = !string.Equals(state.Url, applied.Url, StringComparison.Ordinal);
        var pausedChanged = applied.Paused != state.Paused;
        var drifted = HasDrifted(expected, localPositionMs, localNowMs, applied);
        var seek = urlChanged ? expected > SeekToleranceMs : pausedChanged || drifted;

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
            ScreenId = RoomCode.ScreenIdFor(code),
            Name = string.IsNullOrWhiteSpace(state.Name) ? $"Room {RoomCode.Format(code)}" : state.Name,
            OwnerId = OwnerId,
            CreateIfMissing = true,
            Enabled = viewerLocation.SamePlaceAs(state.Location),
            Provider = state.Provider,
            SourceControlsLocked = true,
            SourceControlsOwnerId = $"Room {RoomCode.Format(code)}",
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
