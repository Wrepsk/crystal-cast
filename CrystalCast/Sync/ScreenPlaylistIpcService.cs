using CrystalCast.Playlist;
using Dalamud.Plugin.Services;

namespace CrystalCast.Sync;

/// <summary>
/// The <c>CrystalCast.Screen.Playlist.*</c> calls. Unlike source locks, these are checked: on the user's
/// screens adding needs the user's "Let other plugins add videos" switch, a plugin may remove only what it
/// added, and Skip, Move, Clear and SetOptions are for the screen's owner (the source-lock holder, or the
/// plugin that created the screen). See <see cref="ScreenPlaylistPolicy"/>.
/// </summary>
internal sealed class ScreenPlaylistIpcService
{
    private const string OwnerOnly = "Only the plugin holding this screen's source lock, or that created it, can do this.";

    private readonly Configuration configuration;
    private readonly ScreenPlaylistAdvancer advancer;
    private readonly Action<PlaylistAdvance> onAdvanced;
    private readonly Action<string> onPlaylistChanged;
    private readonly IPluginLog log;

    public ScreenPlaylistIpcService(
        Configuration configuration,
        ScreenPlaylistAdvancer advancer,
        Action<PlaylistAdvance> onAdvanced,
        Action<string> onPlaylistChanged,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.advancer = advancer;
        this.onAdvanced = onAdvanced;
        this.onPlaylistChanged = onPlaylistChanged;
        this.log = log;
    }

    public string GetJson(string screenId)
    {
        if (!IpcPayloadLimit.TryCheckScreenId(screenId, out var error))
            return Error(error);

        configuration.Normalize();
        var screen = Find(IpcJsonService.NormalizeText(screenId));
        return screen == null
            ? Error($"Screen '{screenId}' was not found.", screenId)
            : Success(screen);
    }

    public string AddJson(string json) => Handle(json, "add a video to", (screen, request) =>
    {
        var ownerId = IpcJsonService.NormalizeText(request.OwnerId);
        if (!ScreenPlaylistPolicy.CanIpcAdd(screen, ownerId))
            return Error(screen.CreatedByIpc
                ? "Only the plugin that created this screen or holds its source lock can add videos to it."
                : "This screen doesn't accept videos from other plugins. The user can allow it in the screen's Playlist tab.", screen.ScreenId);

        // Links only: a bare ID could belong to several sites.
        if (!ScreenPlaylistPolicy.TryCreateEntry(request.Url, BrowserSourceProviderKind.GenericWeb, addedByIpc: true, ownerId, request.Title, out var entry, out var error)
            || !ScreenPlaylistPolicy.TryAdd(screen, entry, out error))
            return Error(error, screen.ScreenId);

        Changed(screen);
        return Success(screen, entry.EntryId);
    });

    public string RemoveJson(string json) => Handle(json, "remove a video from", (screen, request) =>
    {
        var entry = ScreenPlaylistPolicy.FindEntry(screen, IpcJsonService.NormalizeText(request.EntryId));
        if (entry == null)
            return Error($"Entry '{request.EntryId}' is not in this playlist.", screen.ScreenId);

        if (!ScreenPlaylistPolicy.CanIpcRemove(screen, entry, request.OwnerId))
            return Error("A plugin can only remove the videos it added, unless it holds this screen's source lock.", screen.ScreenId);

        ScreenPlaylistPolicy.Remove(screen, entry.EntryId);
        Changed(screen);
        return Success(screen);
    });

    public string ClearJson(string json) => Handle(json, "clear", (screen, request) =>
    {
        if (!ScreenPlaylistPolicy.IsScreenOwner(screen, request.OwnerId))
            return Error(OwnerOnly, screen.ScreenId);

        if (ScreenPlaylistPolicy.Clear(screen))
            Changed(screen);

        return Success(screen);
    });

    public string SkipJson(string json) => Handle(json, "skip in", (screen, request) =>
    {
        if (!ScreenPlaylistPolicy.IsScreenOwner(screen, request.OwnerId))
            return Error(OwnerOnly, screen.ScreenId);

        if (!advancer.TryAdvance(screen, out var advance))
            return Error("Nothing is left to play.", screen.ScreenId);

        configuration.Save();
        onAdvanced(advance);
        return Success(screen, advance.EntryId);
    });

    public string MoveJson(string json) => Handle(json, "reorder", (screen, request) =>
    {
        if (!ScreenPlaylistPolicy.IsScreenOwner(screen, request.OwnerId))
            return Error(OwnerOnly, screen.ScreenId);

        var entryId = IpcJsonService.NormalizeText(request.EntryId);
        if (ScreenPlaylistPolicy.FindEntry(screen, entryId) == null)
            return Error($"Entry '{request.EntryId}' is not in this playlist.", screen.ScreenId);

        if (request.Index is not { } index)
            return Error("Move needs an index.", screen.ScreenId);

        if (ScreenPlaylistPolicy.Move(screen, entryId, index))
            Changed(screen);

        return Success(screen);
    });

    public string SetOptionsJson(string json) => Handle(json, "change the options of", (screen, request) =>
    {
        if (!ScreenPlaylistPolicy.IsScreenOwner(screen, request.OwnerId))
            return Error(OwnerOnly, screen.ScreenId);

        var changed = false;
        if (request.Shuffle is { } shuffle && shuffle != screen.PlaylistShuffle)
        {
            screen.PlaylistShuffle = shuffle;
            changed = true;
        }

        if (request.Loop is { } loop && loop != screen.PlaylistLoop)
        {
            screen.PlaylistLoop = loop;
            changed = true;
        }

        if (changed)
            Changed(screen);

        return Success(screen);
    });

    public static ScreenIpcPlaylistState BuildState(BrowserScreenProfile screen)
    {
        return new ScreenIpcPlaylistState
        {
            Shuffle = screen.PlaylistShuffle,
            Loop = screen.PlaylistLoop,
            AllowIpcAdds = !screen.CreatedByIpc && screen.PlaylistAllowIpcAdds,
            AllowUserAdds = ScreenPlaylistPolicy.AllowsUserAdds(screen),
            CurrentEntryId = screen.PlaylistCurrentEntryId,
            MaxEntries = ScreenPlaylistPolicy.MaxEntries,
            Entries = screen.Playlist.Select(entry => new ScreenIpcPlaylistEntry
            {
                EntryId = entry.EntryId,
                Provider = entry.ProviderKind.ToString(),
                Url = entry.Url,
                Title = ScreenPlaylistPolicy.DisplayTitle(entry),
                Author = entry.Author,
                ThumbnailUrl = entry.ThumbnailUrl,
                DurationSeconds = entry.DurationSeconds,
                AddedByIpc = entry.AddedByIpc,
                AddedBy = entry.AddedBy,
                Played = entry.Played,
                Current = ScreenPlaylistPolicy.IsCurrent(screen, entry),
            }).ToArray(),
        };
    }

    private string Handle(string json, string action, Func<BrowserScreenProfile, ScreenIpcPlaylistRequest, string> handler)
    {
        try
        {
            var request = IpcJsonService.Deserialize<ScreenIpcPlaylistRequest>(json);
            if (request == null)
                return Error("Request body is empty.");

            configuration.Normalize();
            var screenId = IpcJsonService.NormalizeText(request.ScreenId);
            var screen = Find(screenId);
            return screen == null
                ? Error($"Screen '{screenId}' was not found.", screenId)
                : handler(screen, request);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to {Action} a CrystalCast playlist through IPC.", action);
            return Error($"Failed to {action} the playlist: {ex.Message}");
        }
    }

    private void Changed(BrowserScreenProfile screen)
    {
        configuration.Normalize();
        configuration.Save();
        onPlaylistChanged(screen.ScreenId);
    }

    private BrowserScreenProfile? Find(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return configuration.BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }

    private static string Success(BrowserScreenProfile screen, string entryId = "")
    {
        return IpcJsonService.Serialize(new ScreenIpcPlaylistResponse
        {
            Success = true,
            ScreenId = screen.ScreenId,
            EntryId = entryId,
            Playlist = BuildState(screen),
        });
    }

    private static string Error(string error, string screenId = "")
    {
        return IpcJsonService.Serialize(new ScreenIpcPlaylistResponse
        {
            Success = false,
            Error = error,
            ScreenId = IpcJsonService.NormalizeText(screenId),
        });
    }
}
