# Changelog

Notable changes to CrystalCast are documented here. Release tags use the project version without a leading `v`.

## Unreleased

### Added

- Multi-screen browser sources for YouTube, Twitch, Dailymotion, Vimeo, and Generic Web.
- State-only IPC creation, updates, source locking, snapshots, sequencing, and change notifications.
- WebView2 window capture with JPEG fallback, browser controls, adaptive capture cadence, and resource budgets.
- Experimental Wine support using manual WebView2 setup and forced JPEG capture.
- Automated Debug/Release CI, package validation, and tag-based GitHub releases.

### Changed

- Removed local-file playback and FFmpeg support; CrystalCast is browser-only.
- Removed the legacy embedded browser runtime and standardized browser playback on WebView2.
- Refactored provider registration, browser lifecycle ownership, native capture, rendering, and IPC responsibilities.
- Screen height is derived automatically from the captured source aspect ratio.

### Fixed

- Match scene-composite hooks to the actual backbuffer instead of similarly sized textures, and fall back to native overlay output when the hook is unavailable or stalls.
- Use native overlay rendering under Wine, including configurations that previously selected scene compositing; apply game-UI masking to the ImGui output layer.
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
