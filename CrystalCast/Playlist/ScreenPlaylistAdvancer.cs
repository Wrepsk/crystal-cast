using CrystalCast.Ipc;
using CrystalCast.Sources;
using CrystalCast.Video;

namespace CrystalCast.Playlist;

/// <summary>
/// Loads the next playlist video when the current one ends. A video counts as ended only after this screen
/// was seen playing it, so a stream that is offline from the start, or a stale report from the previous
/// video, doesn't skip ahead. Runs on the game's main thread.
/// </summary>
internal sealed class ScreenPlaylistAdvancer
{
    private readonly Func<BrowserScreenProfile, MediaPlaybackTelemetry?> getTelemetry;
    private readonly Func<long> getUnixMs;
    private readonly Random random;
    private readonly Dictionary<string, Watch> watches = new(StringComparer.Ordinal);

    public ScreenPlaylistAdvancer(
        Func<BrowserScreenProfile, MediaPlaybackTelemetry?> getTelemetry,
        Func<long>? getUnixMs = null,
        Random? random = null)
    {
        this.getTelemetry = getTelemetry;
        this.getUnixMs = getUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        this.random = random ?? Random.Shared;
    }

    /// <summary>Checks every screen and returns the ones that moved to their next video.</summary>
    public IReadOnlyList<PlaylistAdvance> Update(Configuration configuration)
    {
        List<PlaylistAdvance>? advanced = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in configuration.BrowserScreens)
        {
            seen.Add(screen.ScreenId);
            // A joined room's screen shows the host's playlist; the host moves it along.
            if (!configuration.Enabled || !screen.Enabled || screen.Playlist.Count == 0 || Rooms.RoomService.IsRoomScreen(screen))
            {
                watches.Remove(screen.ScreenId);
                continue;
            }

            if (Observe(screen) && TryAdvance(screen, out var advance))
                (advanced ??= []).Add(advance);
        }

        foreach (var screenId in watches.Keys.Where(id => !seen.Contains(id)).ToArray())
            watches.Remove(screenId);

        return advanced ?? (IReadOnlyList<PlaylistAdvance>)[];
    }

    /// <summary>Loads the next video now, as the Skip button does. False when nothing is left to play.</summary>
    public bool TryAdvance(BrowserScreenProfile screen, out PlaylistAdvance advance)
    {
        advance = default;
        if (!ScreenPlaylistPolicy.TryPickNext(screen, random, out var pick))
            return false;

        var previousSource = SourceKey(screen);
        ScreenPlaylistPolicy.Load(screen, pick.Entry, pick.StartsOver);
        var sameSource = string.Equals(previousSource, SourceKey(screen), StringComparison.Ordinal);
        watches[screen.ScreenId] = new Watch(SourceKey(screen), getUnixMs(), SawPlaying: false);
        advance = new PlaylistAdvance(screen.ScreenId, pick.Entry.EntryId, sameSource);
        return true;
    }

    /// <summary>Loads a chosen entry, as the Play now button does.</summary>
    public PlaylistAdvance Play(BrowserScreenProfile screen, PlaylistEntry entry)
    {
        var previousSource = SourceKey(screen);
        ScreenPlaylistPolicy.Load(screen, entry);
        watches[screen.ScreenId] = new Watch(SourceKey(screen), getUnixMs(), SawPlaying: false);
        return new PlaylistAdvance(screen.ScreenId, entry.EntryId, string.Equals(previousSource, SourceKey(screen), StringComparison.Ordinal));
    }

    /// <summary>Records what the screen is doing and returns true when its video just ended.</summary>
    private bool Observe(BrowserScreenProfile screen)
    {
        var key = SourceKey(screen);
        if (!watches.TryGetValue(screen.ScreenId, out var watch) || !string.Equals(watch.SourceKey, key, StringComparison.Ordinal))
        {
            watch = new Watch(key, getUnixMs(), SawPlaying: false);
            watches[screen.ScreenId] = watch;
        }

        var telemetry = getTelemetry(screen);

        // Reports from before this source was loaded belong to the previous video.
        if (telemetry == null || telemetry.HostTimestampUnixMs < watch.SinceUnixMs)
            return false;

        if (telemetry.State == ScreenPlaybackState.Playing)
        {
            if (!watch.SawPlaying)
                watches[screen.ScreenId] = watch with { SawPlaying = true };

            return false;
        }

        if (!telemetry.Ended || !watch.SawPlaying || screen.PlaybackPaused)
            return false;

        // Handle each ending once, even if nothing is left to play.
        watches[screen.ScreenId] = watch with { SawPlaying = false };
        return true;
    }

    private static string SourceKey(BrowserScreenProfile screen)
    {
        return $"{screen.ProviderKind}|{BrowserSourceProviderRegistry.GetUrl(screen)}";
    }

    private readonly record struct Watch(string SourceKey, long SinceUnixMs, bool SawPlaying);
}

/// <summary>
/// A screen that loaded a playlist entry. <paramref name="SameSource"/> means the link didn't change (a
/// one-video loop or a repeated link), so the player has to be restarted instead of reloaded.
/// </summary>
internal readonly record struct PlaylistAdvance(string ScreenId, string EntryId, bool SameSource);
