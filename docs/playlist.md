# Playlists

Folder `CrystalCast/Playlist/`, namespace `CrystalCast.Playlist`. The saved data (`PlaylistEntry`, and the playlist fields on `BrowserScreenProfile`) is in `Configuration.cs`.

## Responsibility

Each screen has a list of videos that play after the current one ends. This module holds the rules for that list, notices when a video ended and loads the next, and looks up titles, channels, lengths and thumbnails.

## Data

On `BrowserScreenProfile`:

- `Playlist`: the entries, in order.
- `PlaylistCurrentEntryId`: the entry now loaded on the screen; empty when the current source did not come from the playlist.
- `PlaylistShuffle`, `PlaylistLoop`.
- `PlaylistAllowIpcAdds`: the user's "Let other plugins add videos" switch. No IPC call sets it.
- `PlaylistAllowUserAdds`: on a screen a plugin created, whether the user may add videos. Only the plugin sets it.

`PlaylistEntry`: `EntryId`, `ProviderKind`, `Url`, `Title` (given by the adder), `SourceName` (what the link points at), `MetadataTitle`, `Author`, `ThumbnailUrl`, `DurationSeconds`, `MetadataLookedUp`, `AddedByIpc`, `AddedBy`, `Played`.

## `ScreenPlaylistPolicy`

Static rules; no state of its own.

- `MaxEntries` = 100, `MaxTitleLength` = 200.
- `TryCreateEntry(input, fallbackProvider, addedByIpc, addedBy, title, out entry, out error)`: detects the provider from the link and builds an entry. TODO: document the exact rule for bare IDs; the IPC path passes Generic Web as the fallback so that only full links are accepted.
- `TryAdd`, `Remove`, `Move`, `Clear`, `ResetPlayed`, `FindEntry`, `IndexOf`, `IsCurrent`, `DisplayTitle` (adder's title, else the site's, else the source name).
- `TryPickNext(screen, random, out PlaylistPick)`: candidates are entries not yet played and not current. If there are none and `PlaylistLoop` is on, every entry except the current one becomes a candidate again and the pick is marked `StartsOver` (a one-entry playlist picks that entry). With shuffle the pick is random; otherwise it is the first candidate after the current entry, wrapping to the first.
- `Load(screen, entry, startsOver)`: switches the screen's provider and link to the entry, un-pauses, marks the entry played and current. A web page entry added by a plugin keeps `GenericWebUrlFromIpc`, so it still needs the user's approval.
- `HasNext` / `OverridesVideoLoop`: while another entry is waiting, the video's own loop setting is ignored so the video can end.
- `Normalize(screen)`: trims an over-long list and repairs invalid values; called from `BrowserScreenProfile.Normalize`.

Who may do what over IPC:

- `IsLockOwner`: the caller's `ownerId` equals the screen's source-lock owner while the lock is on.
- `IsScreenOwner`: the lock owner, or on a screen created through IPC, the `ownerId` that created it.
- `CanIpcAdd`: a screen owner, or on a user's screen, anyone while `PlaylistAllowIpcAdds` is on.
- `CanIpcRemove`: a screen owner, or the `ownerId` that added that entry.
- `AllowsUserAdds`: always on the user's screens; on plugin screens only with `PlaylistAllowUserAdds`.

Owner IDs are whatever the calling plugin says they are. These checks keep cooperating plugins apart; the user's switch is the actual control.

## `ScreenPlaylistAdvancer`

Created with a telemetry lookup (`WorldScreenManager.GetPlaybackTelemetry`).

- `Update(configuration)`: called every frame from `Plugin.AdvancePlaylists`. For each enabled screen with a playlist it watches the current source. A video counts as ended only when telemetry newer than the moment the source was loaded says `Ended`, the screen was seen playing that source, and the screen is not paused. Each ending is handled once. It then calls `TryAdvance` and returns the advances made.
- `TryAdvance(screen, out PlaylistAdvance)`: picks and loads the next entry (also used by IPC `Playlist.Skip` and the UI's skip).
- `Play(screen, entry)`: loads a chosen entry ("Play now").

`PlaylistAdvance(ScreenId, EntryId, SameSource)`: `SameSource` means the link did not change (a one-video loop), so the caller must restart the player; a changed link reloads by itself. `ScreenStateIpc.PublishPlaylistAdvance` does that and announces the change.

## `PlaylistMetadataService` and `PlaylistOEmbed`

`PlaylistOEmbed` knows the public oEmbed endpoints of YouTube, Vimeo and Dailymotion: `TryBuildRequestUri(entry, out uri)`, `Parse(provider, json)` into `VideoMetadata(Title, Author, ThumbnailUrl, DurationSeconds)`, and `IsAllowedThumbnail` (an HTTPS image on the site's own image host). Twitch has no such endpoint: `TryDescribeLocally` gives a channel its name and Twitch's public live preview image. Web pages and Twitch VODs get their title and length from playing.

`PlaylistMetadataService`:

- `Update(configuration)`: called every frame, does work at most every 500 ms. Starts background lookups for entries not yet looked up (two at a time), applies finished results on the main thread, fills in title and length of the playing entry from telemetry, and returns the IDs of screens whose playlist changed so the plugin can save and publish.
- `GetThumbnail(entry)`, `GetSourceThumbnail(screen)`: textures for the UI, downloaded once (at most 2 MiB each) and released after two minutes unused.
- `IsLive(entry)`: for Twitch channels, from the live preview (refreshed every five minutes).

Web pages are never fetched outside the browser, so a page a plugin added is not contacted before the user approves it.

## Where it is used

- UI: `Windows/Panels/PlaylistPanel.cs`.
- IPC: `Ipc/ScreenPlaylistIpcService.cs` (`CrystalCast.Screen.Playlist.*`).
- Sources: `BrowserSourceProviderRegistry` consults `OverridesVideoLoop`.
