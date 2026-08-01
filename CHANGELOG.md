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

### Security

- Restricted provider navigation and browser permissions, blocked popups and downloads, isolated provider profiles, authenticated browser telemetry, and bounded IPC/browser payloads.
- Hardened native graphics cleanup, asynchronous browser shutdown, remote-state capacity, and stale-state rejection.

## 0.5.1 - 2026-06-01

- Added WebView2 window capture and browser controls for Generic Web sources.
- Added a client-side IPC toggle and pause-on-zone-change behavior.
- Made browser audio default to muted and disabled the debug marker in Release builds.
