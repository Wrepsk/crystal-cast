# CrystalCast

CrystalCast is a Dalamud API 15 prototype for rendering local-only world-space media screens in FFXIV.

It uses a pinned source checkout of [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) and draws media through `AutoDraw.SceneComposite` with scene-depth occlusion. Implemented sources are local video decoded through FFmpeg and browser-backed YouTube/Twitch screens captured through CEF offscreen rendering or WebView2 capture.

CrystalCast does not download or extract streaming media. Browser sources load the provider's embedded player locally and capture the resulting browser pixels into the same dynamic texture path used for local video.

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

## Usage

Open the controls with `/crystalcast`.

Screens can be enabled, placed in front of the player, rotated/scaled, curved, and switched between source modes. The local video source expects an `ffmpeg.exe` path and a local video file path. Video frames are decoded to BGRA and uploaded into one stable dynamic D3D11 texture so Pictomancy can reuse the same texture handle across frames. Audio is decoded by a second FFmpeg process and played locally through the default Windows output device with a volume slider.

Browser screens currently support YouTube and Twitch. YouTube accepts video URLs/IDs, playlist URLs/IDs, playlist watch URLs, channel IDs, and channel live embeds. Twitch accepts channel and VOD URLs. Browser audio is local to the client when enabled, and browser sources start muted by default.

CrystalCast's intended render output is Pictomancy `SceneComposite`, which composites the world screen into the game backbuffer before native UI/nameplates.

## Browser compatibility

CrystalCast supports two browser capture paths:

- CEF offscreen capture, preferred when compatible.
- WebView2 capture, a Windows fallback for sources that need the Microsoft Edge media stack.

CrystalCast does not ship a proprietary-codec CEF build. Some streaming sources, especially Twitch and YouTube Live, may require codecs not included in the bundled CEF runtime. In Auto mode, CrystalCast prefers CEF offscreen capture when compatible and falls back to WebView2 capture on Windows when needed.

WebView2 capture uses browser screenshot capture rather than a direct raw frame or texture feed, so it may have lower quality or higher overhead than CEF offscreen capture.

| Source | Windows CEF | Windows WebView2 | Notes |
|---|---:|---:|---|
| Local video | N/A | N/A | Decoded through FFmpeg, not the browser path. |
| YouTube videos | Source-dependent | Supported | CEF compatibility depends on the required media codecs. |
| YouTube Live | Partial/source-dependent | Supported | Live streams often need codecs not present in the bundled CEF runtime. |
| Twitch | Partial/source-dependent | Supported | WebView2 is the main compatibility fallback on Windows. |

If CEF or WebView2 is missing or unsupported for a source, CrystalCast reports the failure in the UI/status path and keeps the plugin running.

## IPC

CrystalCast exposes state-only IPC for media-screen synchronization:

- `CrystalCast.ApiVersion`
- `CrystalCast.Screen.GetSnapshot`
- `CrystalCast.Screen.ApplyState`
- `CrystalCast.Screen.Remove`
- `CrystalCast.Screen.LocalStateChanged`
- `CrystalCast.Screen.Create`
- `CrystalCast.Screen.Update`
- `CrystalCast.Screen.UpdateSource`
- `CrystalCast.Screen.SetSourceLock`
- `CrystalCast.Screen.GetSourceState`
- `CrystalCast.Screen.Changed`

The IPC payload intentionally syncs screen pose, source identity, playback state, sequence, host timestamp, and visual flags only. For browser sources, it includes provider-specific source identity and playback telemetry. It does not sync raw pixels, audio, or absolute local file paths.

`CrystalCast.Screen.Create`, `CrystalCast.Screen.Update`, `CrystalCast.Screen.UpdateSource`, and `CrystalCast.Screen.SetSourceLock` accept camel-case JSON strings and return a JSON result with `success`, `error`, `screenId`, and a screen summary when applicable. IPC-created browser screens are marked separately from user-created screens: the normal UI creation limit remains 8 screens, while IPC-created screens can render above that limit up to CrystalCast's render cap.

`Create`/`Update` support `name`, `ownerId`, `enabled`, `activate`, `sourceControlsLocked`, `sourceControlsOwnerId`, `placement`, `provider`, `youtube`, and `twitch` settings. `UpdateSource` supports `screenId`, `ownerId`, `activate`, `provider`, `youtube`, and `twitch` for source-only updates. Source locks protect owner-controlled source and placement fields such as URL, play/pause, seek/progress, loop, autoplay, playback rate, world position, rotation, size, and visual placement from the local UI only; IPC updates remain authoritative. Audio, spatial-audio, browser resolution, and capture FPS remain locally adjustable.

`ownerId` identifies the external integration or sync owner associated with a screen. It is coordination metadata and is not a security boundary. `sourceControlsOwnerId` is displayed in the UI when source controls are locked.

`CrystalCast.Screen.GetSourceState` accepts a screen ID and returns the current provider and source-specific state. For browser screens this includes the configured URL, canonical URL/source identity, title, playback state, position, duration, rate, and host timestamp when available.

`CrystalCast.Screen.Changed` broadcasts a JSON event when CrystalCast observes placement, visual, source, playback, source-lock, IPC-created-screen, or screen-availability changes. The event includes `changes` and the current screen state envelope. When a previously visible local screen is disabled, deleted, or made unavailable by disabling CrystalCast, `state` is `null` so sync consumers can refresh snapshots and remove stale remote screens.

## Safety and scope

CrystalCast is a local media rendering plugin. It does not automate gameplay, send game actions, spoof packets, parse combat data, collect account identifiers, or interact with FFXIV servers.

On source switches and plugin unload, CrystalCast is expected to dispose active frame sources, browser capture objects, dynamic textures, and audio playback resources. Missing browser runtimes, missing FFmpeg paths, bad URLs, and failed media loads should report clear errors without crashing the plugin.
