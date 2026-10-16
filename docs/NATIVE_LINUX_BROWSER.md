# Native Linux browser proof of concept

Status: implemented and independently tested on 2026-10-15/16. **Experimental
default for new Linux/Wine configurations; native game validation is pending.**

The source, installation instructions, codec requirements, architecture/protocol
and reproducible test commands are in
[NativeLinuxBrowser/README.md](../NativeLinuxBrowser/README.md).

## What works in the test environment

* Native WPE WebKit 2.54.1 loads HTTP pages and generated provider HTML with the
  existing player origin, executes JavaScript and exchanges messages through a
  shim for the existing `chrome.webview` interface.
* Completed compositor buffers produce actual uncompressed BGRA frames, using
  leased shared mappings or raw TCP. There is no JPEG encoding or screenshot
  polling in this backend.
* Local H.264/AAC playback works with the reused Generic Web controller, including
  an MSE fragmented-MP4 fixture. Advancing media time, decoded dimensions,
  ready state and changing frame pixels were checked together.
* The user's YouTube sample `GExciu1OHuE` plays through the existing YouTube page,
  both in the native consumer test and through the actual C# frame source.
* The C# adapter feeds existing pooled `VideoFrame` objects and successfully
  uploads them to a Windows WARP D3D11 texture. A local fixture verified stable
  pause, seek/resume and playback rate 0.5 through existing commands/telemetry.
* Native lifecycle tests passed: wrong authentication, private-file permissions,
  three held frame leases, no overwrite while leased, invalid ACK rejection,
  bounded frame drops/resumption, abrupt client EOF, WebKit child-process cleanup,
  oversized packet rejection, startup timeout and removal of runtime files.
* The original setup UI's actual C# readiness probe authenticated against WPE 2.54.1,
  obtained initialized engine and software H.264/AAC factory information without
  loading a page, then exited with its session files removed. Missing helper,
  incompatible engine and cancellation paths are covered by automated tests.

These measurements describe the original system-linked Arch helper. The optional
portable runtime and stronger setup probe are tracked separately in
[distribution verification](LINUX_BROWSER_DISTRIBUTION.md); the current probe
also requires a local page's JavaScript response and rendered pixels.

## Measurement conditions

Windows host: AMD Ryzen 9 9955HX, 16 cores/32 logical processors. Native helper:
Arch container on Ubuntu 24.04 WSL2, kernel 6.6.87.2, WPE 2.54.1, GStreamer 1.28.8.
The helper ran as UID 1000 with WebKit's sandbox enabled. The development
container allowed nested sandbox syscalls with its seccomp option. It did not
have `/dev/dri` or a desktop audio server. Tests used GStreamer's fake audio sink;
**audible audio and GPU acceleration were not tested**.

The 1080p test used a 12-second synthetic 1920 x 1080 60 fps H.264 Baseline/AAC
fragmented MP4, played through MSE. CPU compositing produced `WPEBufferSHM`.
The shared and TCP tests ran sequentially with the same source, not concurrently.
These are short controlled samples, not sustained game-session benchmarks.

| Native helper to Python consumer | Shared mapping | Raw BGRA TCP |
|---|---:|---:|
| Frames received / approximately 12 seconds | 694 | 692 |
| FPS including startup | 57.77 | 57.65 |
| FPS after first second | 60.01 | 59.71 |
| Distinct sampled frame hashes | 686 | 687 |
| First frame | 289 ms | 296 ms |
| Mean export-to-receive age | 2.01 ms | 3.53 ms |
| P95 export-to-receive age | 2.48 ms | 4.40 ms |
| P95 received frame interval after first second | 17.61 ms | 18.23 ms |
| Helper + browser process-tree CPU | 634% | 678% |
| Peak summed process-tree RSS | 601 MiB | 609 MiB |
| Reported helper dropped frames at last periodic sample | 0 | 0 |

100% CPU represents one logical core. `/proc` process-tree samples exclude the
Python consumer, can miss short-lived work between samples, and RSS can count
shared pages more than once. Export-to-receive age starts after WPE imports the
buffer; it excludes decode/render work, game upload and presentation. It is
**not end-to-end display latency**. Periodic helper metrics are not final totals.

The result establishes that the CPU frame bridge can carry a 1080p60 fixture on
this machine. Roughly six to seven cores of CPU work is too costly to treat as a
finished game backend. Shared memory reduced transfer latency, but the next
optimization target should include WebKit compositing/decoding, not just sockets.
No claim is made about sustained 1080p60 in FFXIV, on a Steam Deck, or under DXVK.

## C# bridge results

The standalone Windows probe uses the actual `LinuxBrowserFrameSource`, a
development launcher pointing to native WPE in WSL, raw TCP and WARP D3D11:

| Test | Observed result |
|---|---|
| Local 720p H.264/AAC, 15 seconds including 3 seconds paused and later half speed | 419 consumed frames, 320 distinct sampled hashes; controls assertions passed; helper exit 0 and session removed |
| Pause | Position was 31 ms before pause and remained 31 ms after the pause interval |
| Seek/resume | Seek to 1 second, resumed playing; telemetry reached 3412 ms before the next command |
| Rate | Final telemetry reported playing at rate 0.5 |
| Local fixture D3D upload API call time | Mean 0.240 ms/frame |
| User YouTube video, 20 seconds | 721 consumed frames, 438 distinct sampled hashes; playing at 16.5 seconds, duration 1564.2 seconds; helper exit 0 and session removed |
| YouTube D3D upload API call time | Mean 0.244 ms/frame |

These consumed frame counts include page/player animation and startup, and the
local run deliberately changes playback state. They are **not video FPS or
performance comparisons**. D3D timing covers the WARP upload API call, not GPU
completion/presentation. No full game renderer or Wine/DXVK session was involved.
The production Wine launcher and cross-Wine shared mapping remain untested.

## Provider evidence

| Provider | Evidence and limitations |
|---|---|
| YouTube VOD | `GExciu1OHuE` passed: native run received 1038 frames/743 distinct samples in 30 seconds with advancing playing telemetry; C# run above also passed. An earlier sample `jfKfPfyJRdk` returned player error 150, so it was not counted as playback support. |
| YouTube Live | Not tested. VOD success does not establish live support. |
| Twitch Live | `monstercat` embed SDK initialized, but the 30-second retest stayed paused at time 0, producing only 9 distinct sampled frames. No live playback established; channel availability, provider policy and engine compatibility need further investigation with a known live link. |
| Dailymotion | `x84sh87` sample reported SDK load failure; no playback established. |
| Vimeo | `76979871` sample reported unavailable/not found and follow-on player errors; no playback established. |
| Generic Web | Local accessible H.264/AAC progressive and MSE media passed; arbitrary websites, redirects, login flows, DRM and other codecs are unverified. |

Tests reuse the actual provider HTML exported from CrystalCast, not hand-written
substitute players. Player readiness, MIME capability checks and initialized
SDKs are never counted as playback passes on their own. Provider tests record
messages and telemetry in ignored local artifacts for investigation.

## Remaining blockers and recommendation

1. **Native launch and game integration:** There is no Linux FFXIV/Wine/Proton
   session in this environment. Validate `start.exe /unix`, inherited desktop
   environment, `Z:` mapping, memory-map visibility, audio and renderer upload on
   real launchers. Flatpak/pressure-vessel namespace boundaries and Proton library
   path contamination need explicit host-launch support if the simple path fails.
2. **Deployment:** WPEPlatform 2.0 packages are recent. Arch builds were tested;
   a supported small installation route for Ubuntu, Fedora and Steam Deck has not
   been established. Copying an Arch ELF binary across distributions is not a
   solution. The helper is small; its system engine dependencies are not.
3. **Media coverage:** Reproduce Twitch Live and known playable Vimeo/Dailymotion
   streams, test audible audio and native hardware decoding, then long-duration
   buffering/reconnection. Ephemeral profiles and absent interactive controls
   prevent persistent login/caption/quality-selection workflows for now.
4. **Efficiency:** The mapped CPU fallback works, but its measured CPU load is
   substantial. Investigate accelerated WPE buffer production and DMA-BUF export,
   then a supported Vulkan/Wine/DXVK import bridge with explicit synchronization.
   Keep the CPU path as a reliable fallback. Do not claim zero-copy until measured.
5. **Remaining functionality:** Native interactive input/windows, asynchronous
   IPC navigation approval, stale-file garbage collection after SIGKILL, persistent
   profile policy and multi-screen performance need separate incremental work.

**Assessment:** WPE is the preferred technical backend and is enabled by default
for new Wine configurations. Its buffer-oriented API suits CrystalCast better than a GTK
widget/snapshot solution. Keep WebKitGTK 6.0 as a possible packaging/UI alternative
only if WPE distribution availability proves impractical; it would need its own
continuous rendering integration. The default selection does not establish
native launch, media coverage or sustainable resource usage. These remain to be
verified using the reproducible acceptance checklist in the helper README.
