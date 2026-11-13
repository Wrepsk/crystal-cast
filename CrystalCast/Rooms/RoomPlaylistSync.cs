using CrystalCast.Playlist;
using CrystalCast.Sources;

namespace CrystalCast.Rooms;

/// <summary>
/// The playlist side of a room: the host sends its playlist with the screen, viewers show a copy of it, and
/// when the host allows it, people in the room can send links that the host's CrystalCast adds.
/// </summary>
internal static class RoomPlaylistSync
{
    public const string AddedBy = "Room viewer";
    private const int MaxTextLength = 200;

    public static RoomPlaylistState BuildState(BrowserScreenProfile screen) => new()
    {
        AllowAdds = screen.PlaylistAllowRoomAdds,
        Shuffle = screen.PlaylistShuffle,
        Loop = screen.PlaylistLoop,
        CurrentEntryId = screen.PlaylistCurrentEntryId,
        Entries = screen.Playlist
            .Take(ScreenPlaylistPolicy.MaxEntries)
            .Select(entry => new RoomPlaylistEntry { EntryId = entry.EntryId, Url = entry.Url, Played = entry.Played })
            .ToList(),
    };

    public static string Fingerprint(RoomPlaylistState playlist) => string.Join(
        '',
        playlist.Entries
            .Select(entry => string.Join('|', entry.EntryId, entry.Url, entry.Played))
            .Prepend(string.Join('|', playlist.AllowAdds, playlist.Shuffle, playlist.Loop, playlist.CurrentEntryId)));

    /// <summary>
    /// Makes a joined screen's playlist a copy of the host's. Returns whether anything changed. Entries the
    /// screen already has keep the details CrystalCast looked up for them; new ones are looked up like any
    /// other playlist entry. The copy is only for showing: joined screens never move to their next video
    /// by themselves.
    /// </summary>
    public static bool Mirror(BrowserScreenProfile screen, RoomPlaylistState playlist)
    {
        var before = Fingerprint(BuildState(screen)) + screen.PlaylistAllowUserAdds;
        var known = screen.Playlist
            .GroupBy(entry => (entry.EntryId, entry.Url))
            .ToDictionary(group => group.Key, group => group.First());

        var entries = new List<PlaylistEntry>();
        foreach (var hostEntry in playlist.Entries.Take(ScreenPlaylistPolicy.MaxEntries))
        {
            var entryId = string.IsNullOrWhiteSpace(hostEntry.EntryId) ? Guid.NewGuid().ToString("N") : hostEntry.EntryId;
            if (known.TryGetValue((entryId, hostEntry.Url), out var existing))
            {
                existing.Played = hostEntry.Played;
                entries.Add(existing);
                continue;
            }

            // The host's own playlist may hold web pages; they open here through the usual approval.
            if (!BrowserSourceDetector.TryDetect(hostEntry.Url, BrowserSourceProviderKind.GenericWeb, out var detected))
                continue;

            entries.Add(new PlaylistEntry
            {
                EntryId = entryId,
                ProviderKind = detected.ProviderKind,
                Url = hostEntry.Url,
                SourceName = Trim(detected.Source.DisplayName),
                Played = hostEntry.Played,
            });
        }

        screen.Playlist = entries;
        screen.PlaylistCurrentEntryId = playlist.CurrentEntryId;
        screen.PlaylistShuffle = playlist.Shuffle;
        screen.PlaylistLoop = playlist.Loop;
        screen.PlaylistAllowUserAdds = playlist.AllowAdds;
        return before != Fingerprint(BuildState(screen)) + screen.PlaylistAllowUserAdds;
    }

    /// <summary>
    /// Checks a link before a viewer sends it: only full links to the supported video sites. Web pages aren't
    /// taken from other players, since they would open on the host's own screen without asking.
    /// </summary>
    public static bool TryValidateLink(string? input, out string url, out string error)
    {
        url = string.Empty;
        if (!BrowserSourceDetector.TryDetect(input, BrowserSourceProviderKind.GenericWeb, out var detected))
        {
            error = "Not a link CrystalCast can play.";
            return false;
        }

        if (detected.IsGenericWebFallback)
        {
            error = "Only YouTube, Twitch, Dailymotion and Vimeo links can be added to a room's playlist.";
            return false;
        }

        url = detected.Url;
        error = string.Empty;
        return true;
    }

    /// <summary>The host's side: adds a link someone in the room sent, if the host allows it.</summary>
    public static bool TryAcceptAdd(BrowserScreenProfile screen, string? link, out string error)
    {
        if (!screen.PlaylistAllowRoomAdds)
        {
            error = "Adding videos is off for this room.";
            return false;
        }

        if (!TryValidateLink(link, out var url, out error))
            return false;

        return ScreenPlaylistPolicy.TryCreateEntry(url, BrowserSourceProviderKind.GenericWeb, addedByIpc: true, AddedBy, title: null, out var entry, out error)
            && ScreenPlaylistPolicy.TryAdd(screen, entry, out error);
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        return trimmed.Length <= MaxTextLength ? trimmed : trimmed[..MaxTextLength];
    }
}
