# Changelog

Notable changes to CrystalCast are documented here. Release tags use the project version without a leading `v`.

## Unreleased

### Changed

- IPC API version 11. No released plugin used the earlier versions, so the interface was tidied without a compatibility layer; see [docs/ipc-api.md](docs/ipc-api.md).
  - `Screen.Create`, `Screen.Update` and `Screen.UpdateSource` take one `source` object instead of a `youTube`, `twitch`, `dailymotion`, `vimeo` or `genericWeb` object. `provider` now alone decides the provider; a provider left out keeps the screen's current one.
  - `Screen.GetSourceState` answers with one `source` object instead of one per provider.
  - `Screen.UpdateSource` no longer takes an `ownerId`; it was never used.
  - `Screen.ApplyState` returns the JSON result `Screen.ApplyStateDetailed` used to return; `Screen.ApplyStateDetailed` is gone.
  - `Screen.Remove` returns a JSON result with `success`, `error` and `screenId` instead of a boolean.
  - `Screen.LocalStateChanged` is gone; `Screen.Changed` carries the same state.
  - `Screen.GetSnapshot` returns `localScreens` and `remoteScreens`; the single `local` screen and `remote` are gone.

## 0.9.1.4 - 2026-11-11

### Added

- With **Scene composite**, screens stay visible when you hide the game UI (Windows and Linux). CrystalCast's windows still hide. Turn it off with **Settings > Rendering > Keep screens visible when the game UI is hidden**. Native overlay screens still hide with the UI, since they are part of it.
- With **Scene composite**, screens also stay visible in GPose, so they appear in screenshots. Turn it off with **Settings > Rendering > Keep screens visible in GPose**.
- Placement gizmo **Resize** mode: drag the in-world width handle to change the screen's size; the height follows the video's shape.
- **Face me** in the Placement tab turns a screen toward your camera and levels it, keeping its position.
- Hold **Ctrl** while dragging the gizmo to snap: 0.1 m moves, 15° turns, 0.25 m widths.

### Changed

- The placement gizmo shows only while the Placement tab is open, so its on/off switch is gone.
- The anchors have plainer names: **Fixed in place** (was World), **Follows you** (was Follow player) and **Follows your view** (was Follow camera).
- The gizmo's arrows and rings now follow the screen's own axes (left/right, up/down, toward/away) instead of the world's. **Gizmo axes > World** switches back.

## 0.9.1.3 - 2026-11-11

### Fixed

- A new screen showed "WebView2 browser source unavailable: Object reference not set to an instance of an object" before any video was loaded. Screens without a link no longer start a browser and show their placeholder, and a link that can't be played shows the usual invalid-link message instead of this error.

## 0.9.1.2 - 2026-11-11

The 0.9.1.1 tag was never released: its build could not reach Ubuntu's package servers.

### Fixed

- The plugin manifest's icon link pointed at a placeholder address; it now points at the CrystalCast icon.

## 0.9.1 - 2026-11-11

### Changed

- **Settings > Browser > JPEG capture size** now also offers 480p, 1440p and the screen's own resolution up to 4K, besides 720p and 1080p. Frames never exceed the screen's resolution, so the larger sizes only help screens set above 1080p.
- The **Let other plugins add videos** switch is no longer shown on screens created through IPC. On those screens the plugin that created the screen (its `ownerId`) and the source-lock owner can add videos, skip, reorder, clear and change Shuffle and Loop playlist.
- **Scene composite** is now the default output layer on Linux (Wine) as on Windows, after it was confirmed to work there. The **Try experimental scene composite** switch is gone. Wine configurations saved with the old native-overlay default move to Scene composite once; picking Native overlay afterwards sticks. A scene-composite callback failure now selects native overlay until you switch layers on every platform, not only under Wine.

### Added

- Deleting a screen another plugin created opens a window asking whether to keep it hidden this session (on by default). Hidden screens can't be created again by that plugin until the game restarts; otherwise the screen comes back on the plugin's next update. **Settings > IPC > Hidden screens** shows how many are hidden and can show them again.
- IPC: `createIfMissing` on `Screen.Update` creates the screen when it does not exist. `Screen.Changed` `Unavailable` events carry a `removalReason` (`RemovedByIpc`, `RemovedByUser`, `HiddenByUser`), refused calls for a hidden screen return `hiddenByUser: true`, and `GetCapabilities` lists `features`. IPC API version is now 10.
- The screen list in the main window shows each screen's video thumbnail (YouTube, Vimeo, Dailymotion, and Twitch live previews). Screens with no link, a web page or no picture keep their crystal artwork.
- On screens created through IPC the Playlist tab is read-only for the user: adding, Shuffle, Loop playlist, Skip, Clear, Play now, reordering and removing are all blocked.
- IPC: `playlistAllowUserAdds` on `Screen.Create` and `Screen.Update` for screens created through IPC. Off by default; when on, the user can add videos to that screen, and nothing else. The playlist state reports it as `allowUserAdds`, and `allowIpcAdds` is now always false on those screens.

### Removed

- The **ImGui overlay** output layer is no longer offered in Settings > Rendering; it caused problems when selected. A saved ImGui overlay choice moves to the default, scene composite. CrystalCast also no longer falls back to drawing through ImGui when Dalamud's native overlay isn't available; screens are hidden instead, and Settings > Rendering and the diagnostics say why.

### Fixed

- A browser could keep playing audio after its screen changed browser backend, with no screen showing it, and it survived turning the plugin off. The browser thread could miss the request to close when it arrived while the browser was busy, which is most of the time during playback. Screens now always close their browser, even one still starting, and turning the plugin off in Dalamud ends any CrystalCast browser that is still running.

## 0.9.0 - 2026-11-10

### Added

- Per-screen playlists: a **Playlist** tab (between Audio and Placement) queues videos from any supported site to play after the current one ends, with Shuffle, Loop playlist, Play now, reorder, Skip and Clear. The Source tab's per-video loop is paused while another video is waiting. Entries show the title, channel, length and thumbnail, from YouTube, Vimeo and Dailymotion's oEmbed endpoints, Twitch channels show their name, live preview and Live/Offline; Twitch VODs and web pages take their title from playback.
- IPC: `CrystalCast.Screen.Playlist.Get`, `Add`, `Remove`, `Clear`, `Skip`, `Move` and `SetOptions`. Other plugins can add videos only after the user turns on **Let other plugins add videos** for that screen (or when they hold its source lock), and may remove only the videos they added. `Screen.Changed` reports playlist changes as `Playlist`. IPC API version is now 9.

- IPC: `CrystalCast.Ready` and `CrystalCast.Disposed` events, and `CrystalCast.GetCapabilities` (versions, providers, limits, Wine/WebView2 status).
- IPC: `CrystalCast.Screen.Changed` now also reports changes made inside the player (pause or play in browser controls, a video ending, next playlist item, title loaded, seeks).

### Fixed

- On Linux (Wine), the hidden browser window behind each screen showed up in the top-left corner of the game and could be clicked. It now sits off the screen.
- IPC could change a user-created screen to a Generic Web address that then loaded without the approval prompt. IPC-set Generic Web addresses now need approval on every screen.
- IPC calls from other plugins' background threads could change screens while the game was drawing them. Every call now runs on the game's main thread.
- `Create`, `Update`, `UpdateSource`, `SetSourceLock` and `GetSourceState` now have the same 64 KiB size limit as `ApplyState`.

## 0.8.3 - 2026-11-10

### Changed

- WebView2 JPEG capture (and the JPEG fallback of window capture) now receives frames from the browser's DevTools screencast instead of taking a screenshot each frame. A 60 fps video reaches about 60 fps on Windows (was about 24) and about 55 fps under Wine (was about 13). Frames are decoded off the browser thread. If the screencast can't start, capture goes back to screenshots and the screen's status says why. Set `CRYSTALCAST_WEBVIEW2_SCREENCAST` to `off`, `720` or `1080` to override this for testing.

### Added

- One-click WebView2 for Wine: the Wine setup assistant's **Download and set up** button downloads Microsoft's WebView2 Fixed Version runtime (about 310 MB) into CrystalCast's folder in the game's prefix, checks its Microsoft signature, unpacks it, and sets the Windows 7 mode the WebView2 browser needs under Wine. It needs no installer, winetricks or Linux packages, so it also works in Proton and Steam or Flatpak containers. Browser screens pick it up without a restart, and **Settings > Browser > Downloaded WebView2** shows its size and deletes it again. Winetricks and the native Linux browser are still offered under **Other ways to set this up**.
- **Settings > Browser > JPEG capture size**: 1080p (default) or 720p. 720p uses less CPU for slower machines and Wine setups where JPEG capture stutters.

## 0.8.2 - 2026-11-08

### Added

- Experimental native Linux browser under Wine: **Settings > Browser > Native Linux WebKitGTK (experimental)** plays YouTube, Twitch, Dailymotion and Vimeo screens in the system's WebKitGTK 6.0 and shares finished frames with the game, without WebView2 or JPEG capture. The browser window stays hidden behind other windows; failures and playback stalls are explained in each screen's status and the Dalamud log. Generic Web still uses WebView2.

### Fixed

- WebView2 window capture no longer drops frames while waiting for the game: it rotates three shared textures, so capture always has a free one and the game always reads the newest frame without stalling its render thread. WebView2 is also told not to throttle its hidden window, and on Windows 11 24H2 and newer the capture session's minimum update interval is lowered from Windows' ~16.7 ms default, which held 60 fps videos near 48 fps on high-refresh displays.
- Switching the game between fullscreen and windowed no longer crashes it with scene compositing active: the renderer releases its back-buffer work and flushes Direct3D before the game resizes its swap chain.
- Restores pass NuGet's security audit again: ImageSharp is removed. WebView2 JPEG capture now decodes with the Windows Imaging Component (also provided by Wine), and CrystalRenderer moves to KamiToolKit 2.2.66, which has no ImageSharp dependency. The package no longer ships `SixLabors.ImageSharp.dll`.
- Wine detection now also works on Proton and Wine builds that hide Wine's own functions from Windows programs, so those players get Wine behavior (JPEG WebView2 capture, the native Linux browser option) instead of Windows-only capture paths. Detection never throws, and the diagnostics report shows how Wine was detected.

- Multi-screen browser sources for YouTube, Twitch, Dailymotion, Vimeo, and Generic Web.
- State-only IPC creation, updates, source locking, snapshots, sequencing, and change notifications.
- WebView2 window capture with JPEG fallback, browser controls, adaptive capture cadence, and resource budgets.
- Experimental Wine support using manual WebView2 setup and forced JPEG capture.
- Opt-in experimental scene compositing under Wine, with effective-output/hook diagnostics and automatic overlay fallback; native Linux game-session validation is still pending.
- Automated Debug/Release CI, package validation, and tag-based GitHub releases.

### Changed

- Migrated the screen renderer into the in-repository CrystalRenderer library behind an instance-based backend/frame API, with its own test project and no dependency on CrystalCast media state.
- Removed the Pictomancy submodule and its build/runtime dependency, excluding unused general-shape, decal, and VFX rendering paths; retained source attribution and license text.
- Preserved existing dependency versions, and documented ImageSharp's direct JPEG-decoding use, KamiToolKit transitive use, and current audit failures for separate follow-up.

- Removed local-file playback and FFmpeg support; CrystalCast is browser-only.
- Removed the legacy embedded browser runtime and standardized browser playback on WebView2.
- Refactored provider registration, browser lifecycle ownership, native capture, rendering, and IPC responsibilities.
- Screen height is derived automatically from the captured source aspect ratio.

### Fixed

- Bind explicit depth, video, composite, and UI-mask sampler states; prevent linear interpolation from creating false foreground depths while preserving linear video filtering.
- Use the render camera's steady projection for screen geometry and its jittered projection for depth lookups, keeping both on a shared view snapshot to eliminate screen vibration and transient occlusion cutouts.
- Match scene-composite hooks to the actual backbuffer instead of similarly sized textures, and fall back to native overlay output when the hook is unavailable or stalls.
- Default to native overlay rendering under Wine, including configurations that previously selected scene compositing, unless the user explicitly enables the experimental trial; apply game-UI masking to the ImGui output layer.
- Recover graphics resources after deferred render failures and device loss, clear cancelled draw submissions, and sample camera state with scene depth at composite time.
- Resolve opaque screen intersections with per-pixel depth testing while keeping transparent screen pixels out of the depth-writing pass.
- Clear unavailable scene depth, validate depth texture formats, and skip world-screen rendering until usable depth returns.

### Security

- Restricted provider navigation and browser permissions, blocked popups and downloads, isolated provider profiles, authenticated browser telemetry, and bounded IPC/browser payloads.
- Hardened native graphics cleanup, asynchronous browser shutdown, remote-state capacity, and stale-state rejection.

## 0.5.1 - 2026-06-01

- Added WebView2 window capture and browser controls for Generic Web sources.
- Added a client-side IPC toggle and pause-on-zone-change behavior.
- Made browser audio default to muted and disabled the debug marker in Release builds.
