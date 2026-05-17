# CrystalCast

CrystalCast is a Dalamud API 15 prototype for rendering a local world-space video surface in FFXIV.

It uses a pinned source checkout of [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) at commit `6544d934dc78f16d4a6ce43ba1ddd12854b685d6` and draws a flat `AddImage` panel with scene-depth occlusion. The first implemented sources are a bundled static image, generated BGRA test frames, and local video decoded through `ffmpeg`.

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

If the screen draws over player names or other UI elements, try the `UI mask` modes in `/crystalcast`. `None` is the most compatible baseline; `Backbuffer alpha` or `Backbuffer subtraction` can let UI/nameplate pixels appear above the world screen on setups where Pictomancy can derive a usable UI mask.

## IPC

CrystalCast exposes state-only IPC for a separate sync plugin:

- `CrystalCast.ApiVersion`
- `CrystalCast.Screen.GetSnapshot`
- `CrystalCast.Screen.ApplyState`
- `CrystalCast.Screen.Remove`
- `CrystalCast.Screen.LocalStateChanged`

The IPC payload intentionally syncs screen pose, source identity, playback state, sequence, and visual flags only. It does not sync raw pixels or absolute local file paths.
