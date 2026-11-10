# CrystalCast

CrystalCast is a Dalamud API 15 plugin for rendering local world-space browser screens in FFXIV.

It uses the in-repository [CrystalRenderer](CrystalRenderer/README.md) library to draw world-space screen surfaces with scene-depth occlusion. CrystalRenderer extracts the screen-focused implementation from the CrystalCast-specific Pictomancy fork; Pictomancy is no longer built or packaged. YouTube, Twitch, Dailymotion, Vimeo, and Generic Web screens are captured through WebView2.

CrystalCast does not download or extract streaming media. Browser sources load the provider's embedded player locally and capture the resulting browser pixels for the world-space renderer.

## Build

The repository pins its .NET SDK in `global.json`. A current Dalamud development installation is also required.

1. Restore locked dependencies and build the solution:

   ```powershell
   dotnet restore CrystalCast.sln --locked-mode -p:Platform=x64
   dotnet build CrystalCast.sln -c Debug --no-restore -p:Platform=x64
   ```

2. Add `CrystalCast/bin/x64/Debug/CrystalCast.dll` as a Dalamud dev plugin.

## Tests

The browser-only model, migration, provider URL parsers, source-kind compatibility, screen and runtime budgets, IPC change filtering, remote-state sequencing, browser factory mapping, page isolation, lifecycle races, pooled-frame leases, adaptive tessellation, native graphics error classification, and placement-prediction resets are covered by the xUnit test project:

```powershell
dotnet test CrystalCast.sln -c Debug -p:Platform=x64
```

CrystalCast.Tests covers media/plugin behavior; CrystalRenderer.Tests covers the extracted renderer. The browser tests use an injected frame-source factory and do not start WebView2 or Windows Graphics Capture. Rendering regressions cover output fallback, deferred-error delivery, COM resource identity, depth-state and format policies, and HLSL compilation with the Windows shader compiler. Pixel-level tests render crossing, transparent, and translucent screens using Windows' software D3D11 (WARP) device; they do not require FFXIV or a physical GPU.

Pull requests and pushes to `master` build and test both Debug and Release configurations. Release builds produce `CrystalCast/bin/x64/Release/CrystalCast/latest.zip`; CI verifies its exact runtime contents and generated manifest before uploading it.

## Usage

Open the controls with `/crystalcast`.

Screens can be enabled, placed in front of the player, rotated/scaled, curved, and switched between supported browser providers.

Browser screens currently support YouTube, Twitch, Dailymotion, Vimeo, and Generic Web. YouTube accepts video URLs/IDs, playlist URLs/IDs, playlist watch URLs, channel IDs, and channel live embeds. Twitch accepts channel and VOD URLs. Dailymotion accepts video URLs/IDs and playlist URLs. Vimeo accepts video URLs/IDs, including unlisted video URLs with hashes. Generic Web accepts HTTP/HTTPS page URLs and syncs playback when it can access a page `<video>` or `<audio>` element. Browser audio is local to the client when enabled, and browser sources start muted by default.

Each screen has a **Playlist** tab. Videos added there play after the current one ends, from any supported site, with **Shuffle** and **Loop playlist** switches. A per-video loop setting is paused while another playlist video is waiting, so the current one can end. Twitch streams and most web pages never end on their own; use **Skip** for those. **Let other plugins add videos** is off by default and only the user can turn it on.

Playlist entries show the video's title, channel, length and thumbnail. For YouTube, Vimeo and Dailymotion links CrystalCast asks the site's public oEmbed endpoint when the entry is added (the same site the video would play from) and loads the thumbnail from that site's image host. Twitch has no such endpoint and its stream titles need API credentials, so a Twitch channel shows its name and Twitch's public live preview image (refreshed every five minutes, with Live or Offline). Twitch VODs and web pages are not fetched outside the browser; they get their title and length once they play.

For YouTube, use **Show browser controls** to open the interactive player and change captions, playback quality, playback speed, and other YouTube settings. YouTube no longer exposes programmatic quality selection through the IFrame API, so quality changes use YouTube's own settings menu.

CrystalCast's default render output on every platform, Wine included, is CrystalRenderer `RenderOutput.SceneComposite`, which composites the world screen into the game backbuffer before native UI/nameplates.

An unavailable scene-composite hook or a pending frame older than two seconds automatically selects the native overlay for the current graphics session. Diagnostics report the effective output layer and fallback reason. Switching output layers or reinitializing the graphics session allows another attempt. Scene composite has been confirmed to work under Wine, so Linux uses it by default too; Wine configurations saved with the old native-overlay default are moved to it once. If the native overlay isn't available, screens are hidden rather than drawn through ImGui, and Settings > Rendering says so.

Opaque screen pixels use per-pixel reverse-depth testing, so intersecting solid screens occlude one another correctly. Transparent pixels are drawn afterward without writing depth; intersections between translucent surfaces still use distance sorting. Camera state and scene depth are sampled at the composite pass. If scene depth is unavailable or its format cannot be sampled, world screens are skipped until depth becomes usable again.

**Known DLSS limitation:** foreground object edges can show distortion where they overlap a screen, in both Scene composite and Native overlay output. The tested workaround is to select **AMD FSR** for the game's graphics upscaling and use **TSCMAA** anti-aliasing. CrystalCast does not change these game settings automatically. See [rendering validation and findings](docs/RENDERING_VALIDATION.md) for details.

## Browser compatibility

CrystalCast supports two WebView2 capture paths:

- WebView2 window capture, the preferred Auto path on Windows; it uses Windows Graphics Capture to avoid CPU readback when supported.
- WebView2 JPEG capture, used as the Windows fallback and forced when running under Wine.

In Auto mode on Windows, CrystalCast prefers WebView2 window capture and falls back to WebView2 JPEG capture when Windows Graphics Capture is unavailable.

WebView2 JPEG capture receives JPEG frames from the browser's DevTools screencast (up to 1080p, or 720p with **Settings > Browser > JPEG capture size**) and falls back to browser screenshots if the screencast cannot start. It is not a raw frame or texture feed, so it can have lower quality or higher CPU use than window capture. WebView2 window capture uses Windows Graphics Capture, synchronizes its shared D3D texture with keyed mutexes, and falls back to JPEG capture if the OS, capture session, or graphics device becomes unavailable.

### Experimental Wine support

Microsoft does not officially support WebView2 on Linux or Wine. CrystalCast detects Wine, disables Windows Graphics Capture, and uses JPEG capture only. If WebView2 is missing, first-time Wine users see a setup assistant (also under **Settings > Browser > WebView2 under Wine > Open setup**).

Its **Download and set up** button downloads Microsoft's WebView2 Fixed Version runtime for x64 (about 310 MB, 700 MB unpacked) from Microsoft into `%LOCALAPPDATA%\CrystalCast\WebView2Runtime` inside the game's prefix. The link comes from Microsoft's WebView2 download page, with a built-in link as a fallback, and only HTTPS links on Microsoft's download host are accepted. The archive's Microsoft signature is checked before it is unpacked. Under Wine, a signature whose root certificate the prefix doesn't trust is accepted; an unsigned or modified file is always rejected. The Fixed Version is a plain folder rather than an installer, so it needs nothing on the Linux side and works the same with Wine, Proton, XIVLauncher and Steam or Flatpak containers. The assistant also sets Wine's per-program Windows 7 mode for `msedgewebview2.exe` (`HKCU\Software\Wine\AppDefaults\msedgewebview2.exe`, as `winetricks webview2` does), because the WebView2 browser does not start under Wine without it (Wine bug 58921). An installed WebView2 is still preferred when present. **Settings > Browser > Downloaded WebView2** shows the download's size and deletes it; this closes browser screens first. The game keeps one small file (about 8 MB) loaded once a browser has run, so that file is moved aside and removed the next time the game starts.

`WINEPREFIX="/path/to/your/ffxiv-wine-prefix" winetricks webview2` remains available in the assistant as an alternative. Wine support remains experimental.

Linux users can instead render YouTube, Twitch, Dailymotion and Vimeo screens in the system's native WebKitGTK 6.0 browser (experimental): install WebKitGTK 6.0 with the distribution's package manager and select **Native Linux WebKitGTK (experimental)** under **Settings > Browser**. WebView2 is then not required for those providers. A native helper started through Wine renders the same player page and shares finished frames with the plugin, avoiding WebView2 and JPEG capture. Generic Web still uses WebView2. See the [tester guide](docs/LINUX_WEBKITGTK_TESTING.md) and [helper design](native/webkitgtk-host/README.md).

Missing or stalled scene hooks fall back to native overlay (screens are hidden if the native overlay is unavailable). A non-device-loss scene callback exception also selects native overlay while graphics resources recover, preventing repeated failure/reinitialization loops; switch output layers and back to retry. Copy diagnostics to check the effective layer, fallback reason, hook activity, and scheduled/flushed frame counts. See the [Wine scene-composite checklist](docs/RENDERING_VALIDATION.md#wine-scene-composite-checklist).

Each WebView2 screen serves its player from a unique immutable in-memory page resource. Browser controls and telemetry use per-screen WebMessages protected by an instance nonce, so concurrent screens cannot overwrite a shared page or accept another screen's messages. Browser initialization and capture are cancellation-driven; commands and disposal do not synchronously wait on the render or UI thread.

### Browser security and privacy

Provider URLs accept only credential-free HTTP/HTTPS URLs on the provider's exact host or a real subdomain; lookalike suffixes such as `youtube.com.example.org` are rejected. Provider player documents cannot navigate their top-level browser outside the generated player page. WebView2 blocks popups, downloads, external URL schemes, and sensitive browser permissions such as camera, microphone, location, notifications, and file access. It allows only its media-autoplay permission so host Play commands can operate embedded players. Browser telemetry is accepted only from the expected main document with the current screen nonce, and message size, JSON depth, text, URL, and media-time values are bounded.

WebView2 stores each provider in a separate profile. The Browser settings tab can schedule all CrystalCast browser cookies, local storage, cache, and saved state for deletion on the next plugin load, before a browser runtime starts. Legacy CrystalCast browser-profile locations are included in that cleanup.

Generic Web intentionally has a broader trust surface: it runs CrystalCast's media-control script inside the HTTP/HTTPS page supplied by the user. That page can track the user, navigate to other HTTP/HTTPS pages, and observe or fabricate its own playback telemetry. The nonce prevents cross-screen message confusion, but it does not make an untrusted Generic Web page trustworthy. Load only sites you trust; popups, downloads, external schemes, and sensitive browser permissions remain blocked, while media autoplay is allowed for playback control.

At most eight browser runtimes are active simultaneously. Additional enabled screens remain configured but are deferred in list order; the renderer and Diagnostics tab report this explicitly. GPU texture sampling diagnostics are disabled by default because they introduce a synchronous readback, and can be enabled temporarily from the Diagnostics tab.

| Source | Windows WebView2 | Experimental Wine WebView2 | Notes |
|---|---:|---:|---|
| YouTube videos | Supported | Untested | Wine forces JPEG capture. |
| YouTube Live | Supported | Untested | Wine forces JPEG capture. |
| Twitch | Supported | Untested | Wine forces JPEG capture. |
| Dailymotion | Supported | Untested | Wine forces JPEG capture. |
| Vimeo | Supported | Untested | Wine forces JPEG capture. |
| Generic Web | Supported | Untested | Playback sync is best effort for browser-accessible HTML media; otherwise URL/page capture still works. |

If WebView2 is missing or unsupported for a source, CrystalCast reports the failure in the UI/status path and keeps the plugin running.

## IPC

CrystalCast exposes state-only IPC for media-screen synchronization:

- `CrystalCast.ApiVersion`
- `CrystalCast.GetCapabilities`
- `CrystalCast.Ready` / `CrystalCast.Disposed` (events)
- `CrystalCast.Screen.GetSnapshot`
- `CrystalCast.Screen.ApplyState`
- `CrystalCast.Screen.ApplyStateDetailed`
- `CrystalCast.Screen.Remove`
- `CrystalCast.Screen.LocalStateChanged`
- `CrystalCast.Screen.Create`
- `CrystalCast.Screen.Update`
- `CrystalCast.Screen.UpdateSource`
- `CrystalCast.Screen.SetSourceLock`
- `CrystalCast.Screen.GetSourceState`
- `CrystalCast.Screen.Changed`
- `CrystalCast.Screen.Playlist.Get` / `Add` / `Remove` / `Clear` / `Skip` / `Move` / `SetOptions`

The IPC payload intentionally exchanges screen pose, source identity, playback state, sequence, host timestamp, and visual flags only. Received states are retained for snapshot consumers; CrystalCast does not automatically render remote states. It does not sync raw pixels or audio.

API version 9 runs every call on the game's main thread, so other plugins may call from any thread: calls from other threads wait up to 5 seconds for it and otherwise return an error. Every JSON argument is limited to 64 KiB and screen IDs to 256 characters.

`CrystalCast.Ready` is sent when the IPC becomes available (plugin load, or IPC switched on in settings) and `CrystalCast.Disposed` before it goes away, so integrations can connect without polling `ApiVersion`. `CrystalCast.GetCapabilities` returns JSON with the API and plugin versions, supported providers, screen and payload limits, and the browser situation: Wine, configured backend, capture mode, and whether WebView2 is available (with its version or the reason it is not).

The remote-state store validates browser-only remote states, stores at most 256 remote screens keyed by owner session and screen ID, and expires entries five minutes after their last accepted receipt. `ApplyState` remains a compatibility boolean API. `ApplyStateDetailed` accepts the same JSON state and returns a JSON result distinguishing `Applied`, `IgnoredDuplicate`, `IgnoredStale`, `IgnoredSelf`, `RejectedInvalid`, and `RejectedCapacity`.

`CrystalCast.Screen.Create`, `CrystalCast.Screen.Update`, `CrystalCast.Screen.UpdateSource`, and `CrystalCast.Screen.SetSourceLock` accept camel-case JSON strings and return a JSON result with `success`, `error`, `screenId`, and a screen summary when applicable. IPC-created browser screens are marked separately from user-created screens: the normal UI creation limit remains 8 screens, IPC can create up to 56 screens, and the combined render cap is 64.

`Create`/`Update` support `name`, `ownerId`, `enabled`, `activate`, `sourceControlsLocked`, `sourceControlsOwnerId`, `placement`, `provider`, `youtube`, `twitch`, `dailymotion`, `vimeo`, and `genericWeb` settings, plus `playlistAllowUserAdds` on screens created through IPC (off by default; ignored on the user's own screens). On a screen created through IPC the Playlist tab is read-only for the user, and `playlistAllowUserAdds` unblocks only adding videos. `UpdateSource` supports `screenId`, `ownerId`, `activate`, `provider`, `youtube`, `twitch`, `dailymotion`, `vimeo`, and `genericWeb` for source-only updates. Source locks protect owner-controlled source and placement fields such as URL, play/pause, seek/progress, loop, autoplay, playback rate, world position, rotation, size, and visual placement from the local UI only; IPC updates remain authoritative. Audio, spatial-audio, browser resolution, and capture FPS remain locally adjustable.

A Generic Web address set through IPC needs the user's approval before it loads, on any screen, not only on IPC-created ones. It stays that way until the user types an address for that screen.

`ownerId` identifies the external integration or sync owner associated with a screen. It is coordination metadata and is not a security boundary. `sourceControlsOwnerId` is displayed in the UI when source controls are locked.

`CrystalCast.Screen.GetSourceState` accepts a screen ID and returns the current provider and source-specific state. For browser screens this includes the configured URL, canonical URL/source identity, title, playback state, position, duration, rate, and host timestamp when available.

`CrystalCast.Screen.Playlist.Get` accepts a screen ID and returns the playlist: `shuffle`, `loop`, `allowIpcAdds` (always false on screens created through IPC), `allowUserAdds` (whether the user may add videos in the UI), `currentEntryId`, the limits, and `entries` (each with `entryId`, `provider`, `url`, `title`, `author`, `thumbnailUrl`, `durationSeconds`, `addedByIpc`, `addedBy`, `played`, and `current`). `title` is the one given to `Add`, else the site's title, else what the link points at; details arrive shortly after adding and are announced with a `Playlist` change. The other playlist calls accept a JSON request with `screenId`, `ownerId`, and the call's fields (`url` and an optional `title` for `Add`; `entryId` for `Remove`; `entryId` and `index` for `Move`; `shuffle` and/or `loop` for `SetOptions`), and return `success`, `error`, `screenId`, `entryId`, and the playlist. Unlike source locks, these calls are checked:

- `Add` needs the screen's **Let other plugins add videos** switch, which only the user can change, or the caller's `ownerId` must match the screen's source-lock owner. Screens created through IPC have no such switch: the `ownerId` that created the screen and the source-lock owner can add to them. Only full links are accepted and a playlist holds at most 100 videos. Web pages added this way still need the user's approval before they open.
- `Remove` works on entries added under the same `ownerId`; the screen's owner can remove any entry.
- `Skip`, `Move`, `Clear`, and `SetOptions` are for the screen's owner: the source-lock owner, or on a screen created through IPC, the `ownerId` that created it.

`ownerId` is still reported by the calling plugin, so these checks keep cooperating integrations apart; the user's switch is what controls access. On the user's own screens the user can always remove videos, clear the playlist, and turn the switch off, even when the screen is locked.

`CrystalCast.Screen.Changed` broadcasts a JSON event when CrystalCast observes placement, visual, source, playback, source-lock, IPC-created-screen, or screen-availability changes. Changes made inside the player itself are included as well: pausing in browser controls, a video ending, the next playlist item, a title loading, and seeks (a position more than 2 seconds from where playback should be). These are checked four times a second; normal playback progress does not send events. The event includes `changes` and the current screen state envelope. Playlist edits, the next video starting, and option changes are reported with `Playlist` in `changes`. When a previously visible local screen is disabled, deleted, or made unavailable by disabling CrystalCast, `changes` contains `Unavailable` and `state` is `null` so sync consumers can refresh snapshots and remove stale remote screens.

## Safety and scope

CrystalCast is a local media rendering plugin. It does not automate gameplay, send game actions, spoof packets, parse combat data, collect account identifiers, or interact with FFXIV servers.

On source switches and plugin unload, CrystalCast is expected to dispose active browser frame sources, capture objects, and dynamic textures. Missing browser runtimes, bad URLs, and failed media loads should report clear errors without crashing the plugin.

Configurations created before browser-only version 2 are migrated automatically. Existing browser screens are retained. If the active legacy source was local video, its screens are disabled during migration so browser media does not begin playing unexpectedly; the legacy placement is retained on the first browser screen, but local file and FFmpeg settings are discarded.

## Development and releases

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and release instructions, [CHANGELOG.md](CHANGELOG.md) for notable changes, [SECURITY.md](SECURITY.md) for vulnerability reporting and security boundaries, and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency provenance.

CrystalRenderer is maintained in [its own repository](https://github.com/Wrepsk/crystal-renderer) and pinned here as the `CrystalRenderer` Git submodule, including its test project. Clone with `git clone --recurse-submodules`, or run `git submodule update --init --recursive` in an existing checkout before building. See [renderer architecture](CrystalRenderer/README.md) and [dependency audit notes](docs/DEPENDENCY_AUDIT.md). Tagged numeric versions trigger a Release build, tests, manifest/package verification, and GitHub Release publication.
