using CrystalCast.Video;

namespace CrystalCast.Playlist;

/// <summary>The next playlist entry to load, and whether the playlist starts over to reach it.</summary>
internal readonly record struct PlaylistPick(PlaylistEntry Entry, bool StartsOver);

/// <summary>
/// The rules for a screen's playlist: what can be added, which entry plays next, and who may change it over IPC.
/// The user can always change the playlist from the UI. Over IPC, adding needs the screen's
/// <see cref="BrowserScreenProfile.PlaylistAllowIpcAdds"/> switch, a plugin may remove only the entries it added,
/// and the plugin holding the screen's source lock may do everything. Owner IDs are what the calling plugin says
/// they are, so these checks keep well-behaved plugins apart; the switch is what protects the user.
/// </summary>
internal static class ScreenPlaylistPolicy
{
    public const int MaxEntries = 100;
    public const int MaxTitleLength = 200;

    /// <summary>Repairs a loaded or IPC-edited playlist. Runs every frame, so it only checks structure.</summary>
    public static bool Normalize(BrowserScreenProfile screen)
    {
        var changed = false;
        if (screen.Playlist == null)
        {
            screen.Playlist = [];
            changed = true;
        }

        if (screen.PlaylistCurrentEntryId == null)
        {
            screen.PlaylistCurrentEntryId = string.Empty;
            changed = true;
        }

        if (screen.Playlist.Count == 0)
        {
            if (screen.PlaylistCurrentEntryId.Length > 0)
            {
                screen.PlaylistCurrentEntryId = string.Empty;
                changed = true;
            }

            return changed;
        }

        var removed = screen.Playlist.RemoveAll(entry =>
            entry == null
            || string.IsNullOrWhiteSpace(entry.Url)
            || !BrowserSourceProviderRegistry.IsSupported(entry.ProviderKind));
        changed |= removed > 0;

        if (screen.Playlist.Count > MaxEntries)
        {
            screen.Playlist.RemoveRange(MaxEntries, screen.Playlist.Count - MaxEntries);
            changed = true;
        }

        HashSet<string>? usedIds = null;
        foreach (var entry in screen.Playlist)
        {
            usedIds ??= new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(entry.EntryId) || !usedIds.Add(entry.EntryId))
            {
                entry.EntryId = Guid.NewGuid().ToString("N");
                usedIds.Add(entry.EntryId);
                changed = true;
            }

            if (entry.Title == null || entry.SourceName == null || entry.MetadataTitle == null
                || entry.Author == null || entry.ThumbnailUrl == null || entry.AddedBy == null)
            {
                entry.Title ??= string.Empty;
                entry.SourceName ??= string.Empty;
                entry.MetadataTitle ??= string.Empty;
                entry.Author ??= string.Empty;
                entry.ThumbnailUrl ??= string.Empty;
                entry.AddedBy ??= string.Empty;
                changed = true;
            }

            if (entry.DurationSeconds < 0)
            {
                entry.DurationSeconds = 0;
                changed = true;
            }

            // Entries saved before SourceName existed kept the auto name in Title, which now means
            // "a name the plugin chose" and would hide the site's title. Done once per entry.
            if (entry.SourceName.Length == 0)
            {
                entry.SourceName = BrowserSourceDetector.TryDetect(entry.Url, entry.ProviderKind, out var detected)
                    ? CleanTitle(detected.Source.DisplayName)
                    : CleanTitle(entry.Url);
                if (string.Equals(entry.Title, entry.SourceName, StringComparison.Ordinal))
                    entry.Title = string.Empty;

                changed = true;
            }
        }

        // The current entry is only current while the screen still shows it.
        if (screen.PlaylistCurrentEntryId.Length > 0
            && (FindEntry(screen, screen.PlaylistCurrentEntryId) is not { } current || !IsShowing(screen, current)))
        {
            screen.PlaylistCurrentEntryId = string.Empty;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Turns a pasted link into an entry. Bare IDs count only for <paramref name="currentProvider"/>, as in the
    /// Source tab; pass <see cref="BrowserSourceProviderKind.GenericWeb"/> to accept full links only.
    /// </summary>
    public static bool TryCreateEntry(
        string? input,
        BrowserSourceProviderKind currentProvider,
        bool addedByIpc,
        string addedBy,
        string? title,
        out PlaylistEntry entry,
        out string error)
    {
        entry = null!;
        if (!BrowserSourceDetector.TryDetect(input, currentProvider, out var detected))
        {
            error = "Not a link CrystalCast can play.";
            return false;
        }

        entry = new PlaylistEntry
        {
            ProviderKind = detected.ProviderKind,
            Url = detected.Url,
            Title = CleanTitle(title),
            SourceName = CleanTitle(detected.Source.DisplayName),
            AddedByIpc = addedByIpc,
            AddedBy = addedByIpc ? (addedBy ?? string.Empty).Trim() : string.Empty,
        };
        error = string.Empty;
        return true;
    }

    /// <summary>Adds <paramref name="entry"/> to the end.</summary>
    public static bool TryAdd(BrowserScreenProfile screen, PlaylistEntry entry, out string error)
    {
        if (screen.Playlist.Count >= MaxEntries)
        {
            error = $"The playlist is full ({MaxEntries} videos).";
            return false;
        }

        screen.Playlist.Add(entry);
        error = string.Empty;
        return true;
    }

    /// <summary>Removes an entry. If it is playing, the video keeps playing but no longer counts as from the playlist.</summary>
    public static bool Remove(BrowserScreenProfile screen, string entryId)
    {
        var index = IndexOf(screen, entryId);
        if (index < 0)
            return false;

        screen.Playlist.RemoveAt(index);
        if (string.Equals(screen.PlaylistCurrentEntryId, entryId, StringComparison.Ordinal))
            screen.PlaylistCurrentEntryId = string.Empty;

        return true;
    }

    public static bool Move(BrowserScreenProfile screen, string entryId, int newIndex)
    {
        var index = IndexOf(screen, entryId);
        if (index < 0)
            return false;

        newIndex = Math.Clamp(newIndex, 0, screen.Playlist.Count - 1);
        if (newIndex == index)
            return false;

        var entry = screen.Playlist[index];
        screen.Playlist.RemoveAt(index);
        screen.Playlist.Insert(newIndex, entry);
        return true;
    }

    public static bool Clear(BrowserScreenProfile screen)
    {
        if (screen.Playlist.Count == 0)
            return false;

        screen.Playlist.Clear();
        screen.PlaylistCurrentEntryId = string.Empty;
        return true;
    }

    /// <summary>Whether a video is waiting after the current one, so the current one should not loop.</summary>
    public static bool HasNext(BrowserScreenProfile screen)
    {
        var playlist = screen.Playlist;
        if (playlist == null || playlist.Count == 0)
            return false;

        if (screen.PlaylistLoop)
            return true;

        foreach (var entry in playlist)
        {
            if (!entry.Played && !IsCurrent(screen, entry))
                return true;
        }

        return false;
    }

    /// <summary>Per-video loop would keep the current video from ending, so it is off while a next video waits.</summary>
    public static bool OverridesVideoLoop(BrowserScreenProfile screen) => HasNext(screen);

    /// <summary>
    /// Picks the entry to play after the current one: the next unplayed entry in list order (wrapping to the
    /// top), or a random unplayed one with shuffle on. When everything has played, Loop playlist starts over.
    /// </summary>
    public static bool TryPickNext(BrowserScreenProfile screen, Random random, out PlaylistPick pick)
    {
        pick = default;
        var playlist = screen.Playlist;
        if (playlist == null || playlist.Count == 0)
            return false;

        var candidates = new List<int>(playlist.Count);
        for (var i = 0; i < playlist.Count; i++)
        {
            if (!playlist[i].Played && !IsCurrent(screen, playlist[i]))
                candidates.Add(i);
        }

        var startsOver = false;
        if (candidates.Count == 0)
        {
            if (!screen.PlaylistLoop)
                return false;

            startsOver = true;
            for (var i = 0; i < playlist.Count; i++)
            {
                if (playlist.Count == 1 || !IsCurrent(screen, playlist[i]))
                    candidates.Add(i);
            }
        }

        int chosen;
        if (screen.PlaylistShuffle)
        {
            chosen = candidates[random.Next(candidates.Count)];
        }
        else
        {
            var currentIndex = IndexOf(screen, screen.PlaylistCurrentEntryId);
            chosen = startsOver ? candidates[0] : candidates.FirstOrDefault(index => index > currentIndex, candidates[0]);
        }

        pick = new PlaylistPick(playlist[chosen], startsOver);
        return true;
    }

    /// <summary>Puts <paramref name="entry"/> on the screen and marks it played.</summary>
    public static void Load(BrowserScreenProfile screen, PlaylistEntry entry, bool startsOver = false)
    {
        if (startsOver)
        {
            foreach (var other in screen.Playlist)
                other.Played = false;
        }

        screen.ProviderKind = entry.ProviderKind;
        BrowserSourceProviderRegistry.SetUrl(screen, entry.ProviderKind, entry.Url);

        // A web page another plugin added still needs the user's approval, as with UpdateSource.
        if (entry.ProviderKind == BrowserSourceProviderKind.GenericWeb)
            screen.GenericWebUrlFromIpc = entry.AddedByIpc;

        screen.PlaybackPaused = false;
        entry.Played = true;
        screen.PlaylistCurrentEntryId = entry.EntryId;
    }

    /// <summary>Starts the playlist over without loading anything.</summary>
    public static void ResetPlayed(BrowserScreenProfile screen)
    {
        foreach (var entry in screen.Playlist)
            entry.Played = IsCurrent(screen, entry);
    }

    public static bool IsLockOwner(BrowserScreenProfile screen, string? ownerId)
    {
        return screen.SourceControlsLocked
            && !string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId)
            && string.Equals(screen.SourceControlsOwnerId, ownerId?.Trim(), StringComparison.Ordinal);
    }

    public static bool CanIpcAdd(BrowserScreenProfile screen, string? ownerId)
    {
        return screen.PlaylistAllowIpcAdds || IsLockOwner(screen, ownerId);
    }

    public static bool CanIpcRemove(BrowserScreenProfile screen, PlaylistEntry entry, string? ownerId)
    {
        if (IsLockOwner(screen, ownerId))
            return true;

        var owner = ownerId?.Trim() ?? string.Empty;
        return entry.AddedByIpc
            && owner.Length > 0
            && string.Equals(entry.AddedBy, owner, StringComparison.Ordinal);
    }

    public static PlaylistEntry? FindEntry(BrowserScreenProfile screen, string? entryId)
    {
        var index = IndexOf(screen, entryId);
        return index < 0 ? null : screen.Playlist[index];
    }

    public static int IndexOf(BrowserScreenProfile screen, string? entryId)
    {
        if (string.IsNullOrEmpty(entryId) || screen.Playlist == null)
            return -1;

        return screen.Playlist.FindIndex(entry => string.Equals(entry.EntryId, entryId, StringComparison.Ordinal));
    }

    public static bool IsCurrent(BrowserScreenProfile screen, PlaylistEntry entry)
    {
        return string.Equals(screen.PlaylistCurrentEntryId, entry.EntryId, StringComparison.Ordinal);
    }

    private static bool IsShowing(BrowserScreenProfile screen, PlaylistEntry entry)
    {
        return entry.ProviderKind == screen.ProviderKind
            && string.Equals(BrowserSourceProviderRegistry.GetUrl(screen), entry.Url, StringComparison.Ordinal);
    }

    /// <summary>The name to show: the adding plugin's, then the site's title, then what the link points at.</summary>
    public static string DisplayTitle(PlaylistEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Title))
            return entry.Title;

        if (!string.IsNullOrWhiteSpace(entry.MetadataTitle))
            return entry.MetadataTitle;

        return string.IsNullOrWhiteSpace(entry.SourceName) ? entry.Url : entry.SourceName;
    }

    internal static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var clean = new string(title.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length <= MaxTitleLength ? clean : clean[..MaxTitleLength];
    }
}
