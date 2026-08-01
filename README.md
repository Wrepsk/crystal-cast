# CrystalCast

CrystalCast is a Dalamud API 15 plugin for rendering local world-space browser screens in FFXIV.

It uses a pinned source checkout of [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) and draws media through `AutoDraw.SceneComposite` with scene-depth occlusion. YouTube, Twitch, Dailymotion, Vimeo, and Generic Web screens are captured through WebView2.

CrystalCast does not download or extract streaming media. Browser sources load the provider's embedded player locally and capture the resulting browser pixels for the world-space renderer.

## Build

The repository pins its .NET SDK in `global.json`. A current Dalamud development installation is also required.

1. Clone submodules:

   ```powershell
   git submodule update --init --recursive
   ```

2. Restore locked dependencies and build the solution:

   ```powershell
   dotnet restore CrystalCast.sln --locked-mode -p:Platform=x64
   dotnet build CrystalCast.sln -c Debug --no-restore -p:Platform=x64
   ```

3. Add `CrystalCast/bin/x64/Debug/CrystalCast.dll` as a Dalamud dev plugin.

## Tests

The browser-only model, migration, provider URL parsers, source-kind compatibility, screen and runtime budgets, IPC change filtering, remote-state sequencing, browser factory mapping, page isolation, lifecycle races, pooled-frame leases, adaptive tessellation, native graphics error classification, and placement-prediction resets are covered by the xUnit test project:

```powershell
dotnet test CrystalCast.Tests/CrystalCast.Tests.csproj -c Debug -p:Platform=x64
```

The tests use an injected browser-frame-source factory and do not start WebView2, Windows Graphics Capture, or D3D resources.

Pull requests and pushes to `master` build and test both Debug and Release configurations. Release builds produce `CrystalCast/bin/x64/Release/CrystalCast/latest.zip`; CI verifies its exact runtime contents and generated manifest before uploading it.

## Usage

Open the controls with `/crystalcast`.

Screens can be enabled, placed in front of the player, rotated/scaled, curved, and switched between supported browser providers.

Browser screens currently support YouTube, Twitch, Dailymotion, Vimeo, and Generic Web. YouTube accepts video URLs/IDs, playlist URLs/IDs, playlist watch URLs, channel IDs, and channel live embeds. Twitch accepts channel and VOD URLs. Dailymotion accepts video URLs/IDs and playlist URLs. Vimeo accepts video URLs/IDs, including unlisted video URLs with hashes. Generic Web accepts HTTP/HTTPS page URLs and syncs playback when it can access a page `<video>` or `<audio>` element. Browser audio is local to the client when enabled, and browser sources start muted by default.

CrystalCast's intended render output is Pictomancy `SceneComposite`, which composites the world screen into the game backbuffer before native UI/nameplates.

## Browser compatibility

CrystalCast supports two WebView2 capture paths:

- WebView2 window capture, the preferred Auto path on Windows; it uses Windows Graphics Capture to avoid CPU readback when supported.
- WebView2 JPEG capture, used as the Windows fallback and forced when running under Wine.

In Auto mode on Windows, CrystalCast prefers WebView2 window capture and falls back to WebView2 JPEG capture when Windows Graphics Capture is unavailable.

WebView2 JPEG capture uses browser screenshot capture rather than a direct raw frame or texture feed, so it may have lower quality or higher overhead than window capture. WebView2 window capture uses Windows Graphics Capture, synchronizes its shared D3D texture with keyed mutexes, and falls back to JPEG capture if the OS, capture session, or graphics device becomes unavailable.

### Experimental Wine support

Microsoft does not officially support WebView2 on Linux or Wine. CrystalCast detects Wine, disables Windows Graphics Capture, and uses JPEG capture only. If WebView2 is missing, first-time Wine users see a setup assistant with the command `WINEPREFIX="/path/to/your/ffxiv-wine-prefix" winetricks webview2`.

Use a recent Winetricks release and install WebView2 into the exact Wine prefix that runs FFXIV. Check the generated prefix path before running the command and back up important prefixes first. This path is intentionally manual: CrystalCast does not download installers or modify a Wine prefix itself. Wine support remains experimental and is not yet tested on a real Linux installation.

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

The IPC payload intentionally exchanges screen pose, source identity, playback state, sequence, host timestamp, and visual flags only. Received states are retained for snapshot consumers; CrystalCast does not automatically render remote states. It does not sync raw pixels or audio.

API version 7 validates browser-only remote states, limits each payload to 64 KiB, stores at most 256 remote screens keyed by owner session and screen ID, and expires entries five minutes after their last accepted receipt. `ApplyState` remains a compatibility boolean API. `ApplyStateDetailed` accepts the same JSON state and returns a JSON result distinguishing `Applied`, `IgnoredDuplicate`, `IgnoredStale`, `IgnoredSelf`, `RejectedInvalid`, and `RejectedCapacity`.

`CrystalCast.Screen.Create`, `CrystalCast.Screen.Update`, `CrystalCast.Screen.UpdateSource`, and `CrystalCast.Screen.SetSourceLock` accept camel-case JSON strings and return a JSON result with `success`, `error`, `screenId`, and a screen summary when applicable. IPC-created browser screens are marked separately from user-created screens: the normal UI creation limit remains 8 screens, IPC can create up to 56 screens, and the combined render cap is 64.

`Create`/`Update` support `name`, `ownerId`, `enabled`, `activate`, `sourceControlsLocked`, `sourceControlsOwnerId`, `placement`, `provider`, `youtube`, `twitch`, `dailymotion`, `vimeo`, and `genericWeb` settings. `UpdateSource` supports `screenId`, `ownerId`, `activate`, `provider`, `youtube`, `twitch`, `dailymotion`, `vimeo`, and `genericWeb` for source-only updates. Source locks protect owner-controlled source and placement fields such as URL, play/pause, seek/progress, loop, autoplay, playback rate, world position, rotation, size, and visual placement from the local UI only; IPC updates remain authoritative. Audio, spatial-audio, browser resolution, and capture FPS remain locally adjustable.

`ownerId` identifies the external integration or sync owner associated with a screen. It is coordination metadata and is not a security boundary. `sourceControlsOwnerId` is displayed in the UI when source controls are locked.

`CrystalCast.Screen.GetSourceState` accepts a screen ID and returns the current provider and source-specific state. For browser screens this includes the configured URL, canonical URL/source identity, title, playback state, position, duration, rate, and host timestamp when available.

`CrystalCast.Screen.Changed` broadcasts a JSON event when CrystalCast observes placement, visual, source, playback, source-lock, IPC-created-screen, or screen-availability changes. The event includes `changes` and the current screen state envelope. When a previously visible local screen is disabled, deleted, or made unavailable by disabling CrystalCast, `changes` contains `Unavailable` and `state` is `null` so sync consumers can refresh snapshots and remove stale remote screens.

## Safety and scope

CrystalCast is a local media rendering plugin. It does not automate gameplay, send game actions, spoof packets, parse combat data, collect account identifiers, or interact with FFXIV servers.

On source switches and plugin unload, CrystalCast is expected to dispose active browser frame sources, capture objects, and dynamic textures. Missing browser runtimes, bad URLs, and failed media loads should report clear errors without crashing the plugin.

Configurations created before browser-only version 2 are migrated automatically. Existing browser screens are retained. If the active legacy source was local video, its screens are disabled during migration so browser media does not begin playing unexpectedly; the legacy placement is retained on the first browser screen, but local file and FFmpeg settings are discarded.

## Development and releases

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and release instructions, [CHANGELOG.md](CHANGELOG.md) for notable changes, [SECURITY.md](SECURITY.md) for vulnerability reporting and security boundaries, and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency provenance.

The Pictomancy submodule intentionally points to a pinned CrystalCast-specific fork derived from the upstream Pictomancy project. Tagged numeric versions trigger a Release build, tests, manifest/package verification, and GitHub Release publication.
