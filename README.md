# CrystalCast

CrystalCast is a Dalamud API 15 prototype for rendering local world-space browser screens in FFXIV.

It uses a pinned source checkout of [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) and draws media through `AutoDraw.SceneComposite` with scene-depth occlusion. YouTube, Twitch, Dailymotion, Vimeo, and Generic Web screens are captured through CEF offscreen rendering or WebView2 capture.

CrystalCast does not download or extract streaming media. Browser sources load the provider's embedded player locally and capture the resulting browser pixels for the world-space renderer.

## Build

1. Clone submodules:

   ```powershell
   git submodule update --init --recursive
   ```

2. Build the solution:

   ```powershell
   dotnet build CrystalCast.sln -c Debug -p:Platform=x64
   ```

3. Add `CrystalCast/bin/x64/Debug/CrystalCast.dll` as a Dalamud dev plugin.

## Tests

The browser-only model, migration, provider URL parsers, source-kind compatibility, screen limits, IPC patching, remote-state sequencing, browser factory mapping, page isolation, and lifecycle races are covered by the xUnit test project:

```powershell
dotnet test CrystalCast.Tests/CrystalCast.Tests.csproj -c Debug -p:Platform=x64
```

The tests use an injected browser-frame-source factory and do not start CEF, WebView2, Windows Graphics Capture, or D3D resources.

## Usage

Open the controls with `/crystalcast`.

Screens can be enabled, placed in front of the player, rotated/scaled, curved, and switched between supported browser providers.

Browser screens currently support YouTube, Twitch, Dailymotion, Vimeo, and Generic Web. YouTube accepts video URLs/IDs, playlist URLs/IDs, playlist watch URLs, channel IDs, and channel live embeds. Twitch accepts channel and VOD URLs. Dailymotion accepts video URLs/IDs and playlist URLs. Vimeo accepts video URLs/IDs, including unlisted video URLs with hashes. Generic Web accepts HTTP/HTTPS page URLs and syncs playback when it can access a page `<video>` or `<audio>` element. Browser audio is local to the client when enabled, and browser sources start muted by default.

CrystalCast's intended render output is Pictomancy `SceneComposite`, which composites the world screen into the game backbuffer before native UI/nameplates.

## Browser compatibility

CrystalCast supports two browser capture paths:

- CEF offscreen capture, preferred when compatible.
- WebView2 JPEG capture, a Windows fallback for sources that need the Microsoft Edge media stack.
- WebView2 window capture, an experimental Windows Graphics Capture path that can avoid the JPEG readback path when supported.

CrystalCast does not ship a proprietary-codec CEF build. Some streaming sources, especially Twitch and YouTube Live, may require codecs not included in the bundled CEF runtime. In Auto mode, CrystalCast uses each provider's preferred browser path and falls back between CEF offscreen capture and WebView2 capture on Windows when needed.

WebView2 JPEG capture uses browser screenshot capture rather than a direct raw frame or texture feed, so it may have lower quality or higher overhead than CEF offscreen capture. WebView2 window capture uses Windows Graphics Capture and falls back to JPEG capture if the OS or capture session is unavailable.

Each WebView2 screen serves its player from a unique immutable in-memory page resource. Browser controls and telemetry use per-screen WebMessages protected by an instance nonce, so concurrent screens cannot overwrite a shared page or accept another screen's messages. Browser initialization and capture are cancellation-driven; commands and disposal do not synchronously wait on the render or UI thread.

| Source | Windows CEF | Windows WebView2 | Notes |
|---|---:|---:|---|
| YouTube videos | Source-dependent | Supported | CEF compatibility depends on the required media codecs. |
| YouTube Live | Partial/source-dependent | Supported | Live streams often need codecs not present in the bundled CEF runtime. |
| Twitch | Partial/source-dependent | Supported | WebView2 is the main compatibility fallback on Windows. |
| Dailymotion | Partial/source-dependent | Supported | WebView2 is the main compatibility fallback on Windows. |
| Vimeo | Partial/source-dependent | Supported | WebView2 is the main compatibility fallback on Windows. |
| Generic Web | Not supported in v1 | Supported | Playback sync is best effort for browser-accessible HTML media; otherwise URL/page capture still works. |

If CEF or WebView2 is missing or unsupported for a source, CrystalCast reports the failure in the UI/status path and keeps the plugin running.

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
