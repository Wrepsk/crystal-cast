# Changelog

Notable changes to CrystalCast are documented here. Release tags use the project version without a leading `v`.

## Unreleased

### Added

- Multi-screen browser sources for YouTube, Twitch, Dailymotion, Vimeo, and Generic Web.
- State-only IPC creation, updates, source locking, snapshots, sequencing, and change notifications.
- WebView2 window capture with JPEG fallback, browser controls, adaptive capture cadence, and resource budgets.
- Experimental Wine support using manual WebView2 setup and forced JPEG capture.
- Experimental scene compositing under Wine, with effective-output/hook diagnostics and automatic overlay fallback; native Linux game-session validation is still pending.
- Native Linux WPE WebKit browser proof of concept: separate system-linked helper, authenticated local control bridge, leased shared BGRA frames with raw TCP fallback, and reuse of existing provider/player logic. YouTube VOD and local H.264/AAC/MSE were verified independently; native Wine launch, audio, wider provider coverage and GPU interoperability remain pending.
- Linux browser selector with backend-specific setup: WebView2 opens its Wine setup screen; native WPE performs an asynchronous helper/engine readiness check and shows installation guidance on failure, with retry and separate dismissal preferences.
- Distribution-aware native setup guidance using host `os-release` metadata, separate handling for SteamOS/Fedora Atomic/runtime environments, and distro-selectable Debug previews. Ubuntu/Fedora prerequisites are explicitly distinguished from an unavailable/unverified complete WPE installer.
- Automated Debug/Release CI, package validation, and tag-based GitHub releases.

### Changed

- New Linux/Wine configurations default to Scene composite with its Wine trial enabled and native WPE browser capture. Explicitly saved choices are retained; Windows defaults and scene-composite fallback are unchanged.
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
- Use native overlay when the Wine scene-composite trial is disabled; apply game-UI masking to the ImGui output layer.
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
