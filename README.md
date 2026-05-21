# CrystalCast

CrystalCast is a Dalamud API 15 prototype for rendering a local world-space media surface in FFXIV.

It uses a pinned source checkout of [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) and draws a flat `AddImage` panel through `AutoDraw.SceneComposite` with scene-depth occlusion. Implemented sources are local video decoded through `ffmpeg` and a first-pass YouTube browser source rendered through WebView2 capture.

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

The panel can be enabled, placed in front of the player, rotated/scaled, and switched between source modes. The local video source expects an `ffmpeg.exe` path and a local video file path. Video frames are decoded to BGRA and uploaded into one stable dynamic D3D11 texture so Pictomancy can reuse the same texture handle across frames. Audio is decoded by a second FFmpeg process and played locally through the default Windows output device with a volume slider.

The YouTube browser source expects a YouTube URL or 11-character video ID. Each client loads the embedded YouTube player locally through the YouTube IFrame API; CrystalCast captures the browser pixels into the same dynamic texture path and does not download or extract YouTube media. The first pass uses WebView2's local browser audio when enabled, or starts muted by default. If the Microsoft Edge WebView2 Runtime is unavailable, the source reports that in the status panel and leaves the plugin running.

CrystalCast's intended render output is Pictomancy `SceneComposite`, which composites the world screen into the game backbuffer before native UI/nameplates.

## IPC

CrystalCast exposes state-only IPC for a separate sync plugin:

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

The IPC payload intentionally syncs screen pose, source identity, playback state, sequence, host timestamp, and visual flags only. For YouTube, it includes the video ID/canonical URL or playlist/live-channel URL and playback position/rate telemetry. It does not sync raw pixels, audio, or absolute local file paths.

`CrystalCast.Screen.Create`, `CrystalCast.Screen.Update`, `CrystalCast.Screen.UpdateSource`, and `CrystalCast.Screen.SetSourceLock` accept camel-case JSON strings and return a JSON result with `success`, `error`, `screenId`, and a screen summary when applicable. IPC-created YouTube screens are marked separately from user-created screens: the normal UI creation limit remains 8 screens, while IPC-created screens can render above that limit up to CrystalCast's render cap.

`Create`/`Update` support `name`, `ownerId`, `enabled`, `activate`, `sourceControlsLocked`, `sourceControlsOwnerId`, `placement`, and `youtube` settings. `UpdateSource` supports `screenId`, `ownerId`, `activate`, `provider`, and `youtube` for source-only updates. YouTube sources may be a video URL/ID, playlist URL/ID, playlist watch URL, channel ID, or channel-ID live embed. Source locks protect owner-controlled source and placement fields such as YouTube URL, play/pause, seek/progress, loop, autoplay, playback rate, world position, rotation, size, and visual placement from the local UI only; IPC updates remain authoritative. Audio, spatial-audio, browser resolution, and capture FPS remain locally adjustable.

`CrystalCast.Screen.GetSourceState` accepts a screen ID and returns the current provider and source-specific state. For YouTube screens this includes the configured URL, canonical URL, video ID, title, playback state, position, duration, rate, and host timestamp.

`CrystalCast.Screen.Changed` broadcasts a JSON event when CrystalCast observes placement, visual, source, playback, source-lock, IPC-created-screen, or screen-availability changes. The event includes `changes` and the current screen state envelope. When a previously visible local screen is disabled, deleted, or made unavailable by disabling CrystalCast, `state` is `null` so sync consumers can refresh snapshots and remove stale remote screens.
