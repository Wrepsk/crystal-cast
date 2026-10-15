# Experimental native Linux browser

This is a working WPE WebKit proof of concept, not a supported replacement for
WebView2 yet. The native helper loads CrystalCast's existing provider pages,
executes JavaScript, exchanges the existing playback messages, and exports real
compositor frames. The plugin consumes those frames through `IVideoFrameSource`.
Windows continues to use WebView2. No browser engine is bundled with the plugin.

See [measured results and remaining blockers](../docs/NATIVE_LINUX_BROWSER.md).

## Backend choice

| Concern | WebKitGTK 6.0 | WPE WebKit 2.54+ |
|---|---|---|
| Browser/media engine | WebKit and GStreamer | WebKit and GStreamer |
| Main embedding model | GTK 4 widget | WPEPlatform view and completed buffers |
| Offscreen video delivery | Requires an appropriate GTK rendering integration; repeatedly requesting snapshots is unsuitable as the final video pipeline | Built-in headless platform and `WPEView::buffer-rendered` |
| GPU path | GTK/GDK render integration | SHM and DMA-BUF buffer types; importing into Wine remains separate work |
| Desktop browser controls | GTK is convenient | Input/window integration must be added |
| Distribution availability | More broadly packaged on the distributions investigated | The new API requires recent packages; this is the principal deployment obstacle |

WPE is the better technical fit for a frame producer. Version 2.54 makes
WPEPlatform the stable default and includes headless output; the legacy
`libwpe`/WPEBackend-fdo export API is unnecessary here.
[WPE release notes](https://wpewebkit.org/blog/2026-09-16-wpewebkit-2.54.html),
[WPE architecture](https://wpewebkit.org/about/architecture.html).

WebKitGTK 6.0 is an API namespace, not an engine version. Both ports share much
of their engine and multimedia implementation. Switching between them would
not by itself fix a provider rejecting WebKit, a missing codec, or DRM support.
[Graphics implementation](https://docs.webkit.org/Ports/WebKitGTK%20and%20WPE%20WebKit/Graphics.html),
[multimedia implementation](https://docs.webkit.org/Ports/WebKitGTK%20and%20WPE%20WebKit/Multimedia.html).

## Build and installation

The validated build is **x86-64 Arch Linux, WPE WebKit 2.54.1**, running in an
unprivileged development container on WSL2. A native desktop/game installation
has not been validated. Build on the target distribution: the Arch binary is
not a portable Ubuntu/Fedora binary.

On a current Arch installation:

```sh
sudo pacman -Syu --needed gcc make pkgconf wpewebkit \
  gst-plugins-base gst-plugins-good gst-plugins-bad gst-plugins-ugly gst-libav
cd NativeLinuxBrowser
pkg-config --modversion wpe-webkit-2.0 wpe-platform-2.0
make
make install PREFIX="$HOME/.local"
"$HOME/.local/bin/crystalcast-browser-helper" --diagnose
```

Run the helper as your desktop user. It refuses browser startup as root and
does not disable WebKit's sandbox. Installation does not modify a Wine prefix.
The ELF helper dynamically links system libraries; the measured development
binary is about 130 KB with debug symbols. System WebKit/GStreamer installations
are substantially larger and remain the distribution's responsibility.

The build needs `wpe-webkit-2.0 >= 2.54`, `wpe-platform-2.0`, `json-glib-1.0`,
`gstreamer-1.0`, GLib/GIO and a C11 compiler. Runtime also needs WebKit's matching
auxiliary processes/resources, CA certificates, fonts, its sandbox dependencies,
and a desktop audio service for audible playback. Installing only a copied
shared library is insufficient.

| Distribution | Installation status and procedure |
|---|---|
| Arch | Package-based compilation above was exercised in the Arch container. Desktop Wine/Proton remains untested. Check the `pkg-config` version gate after package installation. |
| Ubuntu 24.04 | The configured Ubuntu WSL repositories did not provide the required WPEPlatform 2.0 development modules. `libwpewebkit-1.0-dev` from older Ubuntu releases is a different API. No supported native installation recipe yet. Use the development container for independent testing, or obtain a maintained distribution build of WPE 2.54+ before running `make`. |
| Fedora | WebKitGTK 6.0 development packages exist; availability of compatible WPEPlatform 2.0 packages was not established. After obtaining matching WPE packages, require `pkg-config --atleast-version=2.54 wpe-webkit-2.0` and `pkg-config --exists wpe-platform-2.0 json-glib-1.0 gstreamer-1.0`, then build on Fedora. GTK packages alone cannot build this helper. |
| Steam Deck / SteamOS | Not validated. Do not replace SteamOS libraries or disable its read-only protection for this experiment. A maintained user-space/system-extension package and launcher namespace testing are still needed. Being Arch-derived does not establish compatible packages. |

Package references: [Arch WPE](https://archlinux.org/packages/extra/x86_64/wpewebkit/),
[Ubuntu WPE search](https://packages.ubuntu.com/search?keywords=wpewebkit&searchon=sourcenames&section=all&suite=all),
[Fedora WebKitGTK 6.0](https://packages.fedoraproject.org/pkgs/webkitgtk/webkitgtk6.0-devel/),
[upstream installation options](https://wpewebkit.org/about/get-wpe.html).
No automatic engine download or source build is implemented.

### Distribution-aware setup screen

The setup screen reads Linux `os-release` data through Wine's `Z:` mapping even
when the helper is missing. It prefers exposed host metadata at
`/run/host/etc/os-release` or `/run/host/os-release`, then `/etc/os-release` and
`/usr/lib/os-release`. It displays the detected name and metadata source. Reading
these files does not execute their shell-like contents. Missing or inaccessible
host metadata in a launcher sandbox can prevent correct host identification;
known Steam/Freedesktop runtime IDs receive no guessed host install command.

* Arch and recognized Arch-family systems receive the tested `pacman` dependency
  recipe, followed by the WPE version check and helper build commands. Derivative
  repository versions still need to pass the check.
* Ubuntu/Debian receive `apt-get` **build/media prerequisite** commands and an
  `apt-cache` WPE search. These do not install the required engine. There is no
  verified complete WPE 2.54+ apt recipe in this implementation.
* Fedora receives `dnf` **build prerequisite** commands and `dnf provides` queries
  for the required WPE pkg-config modules. These do not establish that an enabled
  repository provides a sufficiently new engine or the needed media decoders.
* SteamOS and recognized Fedora Atomic/immutable variants receive dedicated
  guidance without ordinary mutable-system package commands. Unknown systems
  receive the requirements and version check without a guessed package manager.

The helper build command is gated by the exact pkg-config requirements. Commands
are displayed/copied for the user; CrystalCast does not install packages or change
repositories. The Debug setup preview has Arch, Ubuntu, Fedora, SteamOS, Fedora
Atomic and unknown/runtime samples so every instruction variant can be inspected
on Windows without changing configuration or launching a native helper.

Package/metadata references:
[Flatpak host filesystem exposure](https://docs.flatpak.org/en/latest/sandbox-permissions.html),
[DNF provider queries](https://dnf.readthedocs.io/en/latest/command_ref.html#provides-command),
[Fedora compiler metadata dependencies](https://packages.fedoraproject.org/pkgs/pkgconf/pkgconf-pkg-config/),
[Fedora GStreamer development package](https://packages.fedoraproject.org/pkgs/gstreamer1/gstreamer1-devel/).

### What the helper is

`crystalcast-browser-helper` is CrystalCast's small native Linux executable. The
plugin runs inside Windows Dalamud under Wine, while WPE and GStreamer expose
native Linux libraries. The helper hosts those libraries in their own Linux
process and bridges playback commands, telemetry and frames to the Windows
plugin. Install it once; CrystalCast starts/stops a helper automatically for each
active screen. Users do not need to launch it manually during normal playback.
It is not a second bundled browser engine and does not edit a Wine prefix.

## Media dependencies

Use `--diagnose` and `gst-inspect-1.0` to inspect the actual host, then run playback
tests. Plugin presence and `canPlayType()` results are not proof of playback.

| Media | Practical GStreamer components |
|---|---|
| MP4 H.264/AAC | `qtdemux`, `h264parse`, `avdec_h264`, and `avdec_aac` or another working AAC decoder; base/good/bad plus libav are the exercised package set |
| WebM VP8/VP9 + Opus/Vorbis | Matroska demux, VP8/VP9 decoders, `opusdec`/Vorbis decoders; base/good packages are important |
| AV1 | A compatible software/hardware decoder such as the distribution's libav/dav1d implementation; not tested here |
| HLS/live | Suitable demux/adaptive-streaming plugins plus the stream's codecs and working provider JavaScript; plugin presence alone does not establish Twitch Live support |
| Audio | GStreamer's PulseAudio/PipeWire-compatible output and the user's session audio server; audible output is untested here |
| Hardware decoding | Matching GStreamer VA/V4L2/NVIDIA elements, driver and sandbox device access; not established by this PoC |

The helper enables MediaSource and permits autoplay. It deliberately uses CPU
compositing for a reliable initial frame path. Native video decoding can still
depend on the host's plugin selection, but no hardware acceleration is claimed.
DRM/CDM availability, provider authentication, codec patent/licensing obligations
and geographic/account restrictions are separate concerns; native WebKit does
not guarantee every stream will play.

## Enable in CrystalCast

1. Install the helper and dependencies on the Linux host. Ensure the game inherits
   `HOME` and `XDG_RUNTIME_DIR` from the desktop session.
2. Under Wine, open **Settings > Browser > Linux browser backend** and select
   **Native WPE WebKit (default, experimental)**. The equivalent config flag is
   `EnableNativeLinuxBrowser`. New Wine configurations select this backend and
   Scene composite output, with the experimental Wine rendering trial enabled.
3. Start with one screen, preferably the tested YouTube sample or a local fixture.
   Copy Diagnostics and inspect source status, received frames and read/copy time.
4. Select **WebView2 JPEG capture** to use the existing Wine browser path instead.

The flag defaults on under Wine and off on Windows; saved choices are retained.
It is ignored on Windows. Enabling it skips WebView2's
runtime check/setup assistant. A missing helper reports an error without starting
WebView2. There is no silent fallback requiring Winetricks; fallback is explicit.
Changing the flag recreates sources through the existing screen lifecycle.
When WPE is selected, a background check starts a temporary helper, authenticates
the bridge and requests engine/codec information after WebKit initialization.
The probe never loads a website. EOF stops it and cleans its private runtime
files. Missing helper, incompatible version, dependency or connection failures
open the WPE setup screen with status, build/package guidance and retry. Missing
software codec factories are warnings rather than proof that all playback fails.
Selecting WebView2 opens its Wine setup screen and cancels any WPE probe; startup
only prompts automatically when the selected backend is unavailable. Each
backend has its own saved dismissal preference, and explicit selection/retry
can reopen setup. The Browser tab also exposes the latest WPE detection status.

The launcher searches `~/.local/bin`, `/usr/local/bin`, and `/usr/bin`, then uses
Wine's `start.exe /wait /unix` with separate arguments. The helper's endpoint is
read through Wine's normal `Z:` mapping of the inherited runtime directory.
This path is implemented and covered by policy/protocol tests, but **actual
Wine/Proton launch has not been exercised**. Custom drive mappings, Flatpak
XIVLauncher filesystem/network namespaces, Steam pressure-vessel and inherited
Proton library paths may require host-launch integration. A helper visible only
outside the game's sandbox will not automatically be accessible. No prefix
changes or sandbox escape commands are attempted.

## Frame and control bridge

```text
Existing CrystalCast provider page / Generic Web controller
        | existing nonce-authenticated JS commands and telemetry
Native WPE helper -> completed WPE SHM buffer -> leased BGRA frame slots
        | loopback CCB1 control + frame notifications (or raw BGRA TCP)
LinuxBrowserFrameSource -> pooled VideoFrame -> existing D3D11 texture uploader
        -> CrystalRenderer -> Wine/DXVK game output
```

Only the helper and adapter are new. Provider/player JavaScript, playback intent,
telemetry validation, source descriptors, screen budgets, synchronization and
renderer interfaces are reused. Each source has its own helper and ephemeral
profile; the existing multiple-screen architecture is retained, but only one
screen is validated. Interactive browser windows/input/login are not implemented.
Play/pause/seek, rate, loop and audio settings use the existing command protocol.

The CPU baseline exports little-endian WPE ARGB8888 as tightly packed BGRA. It
supports up to 1920 x 1080. There are three 8,294,400-byte slots in a private
`frames.bin` mapping (about 23.7 MiB reserved per helper). The consumer copies
pixels to a pooled `VideoFrame` before acknowledging the exact slot/sequence.
Unacknowledged slots are never overwritten. At most one pending frame is kept;
slow consumers cause bounded frame dropping. Mapping failure selects raw BGRA
TCP before the handshake. Neither transport compresses pixels or calls a
screenshot API.

1080p60 BGRA is about 498 MB/s of pixel data before extra copies. Shared mapping
avoids socket payload copies but still copies into the plugin's pooled frame and
uploads to D3D11. DMA-BUF could avoid CPU readback on native Linux, but a Linux fd
is not a DXGI shared HANDLE. D3D11 under [DXVK](https://github.com/doitsujin/dxvk)
adds Vulkan/Wine ownership, adapter identity, image format/modifier and fence
requirements. A native Vulkan bridge or a supported Wine/DXVK interoperability
extension must be investigated separately; casting a DMA-BUF fd is invalid.
GPU interoperability is not implemented. Current code rejects non-SHM buffers
rather than assuming their pixel layout.

## IPC and lifecycle

* One helper accepts one authenticated consumer on IPv4 loopback only. The
  endpoint holds a random 256-bit token in a 0600 file, inside a UID-owned 0700
  session directory beneath `$XDG_RUNTIME_DIR/crystalcast`. Credentials are not
  placed in arguments, logs or diagnostics. Existing session directories are
  rejected; symlinks/wrong-owner/permissive runtime directories are rejected.
* CCB1 uses a 16-byte little-endian header: magic `0x31424343`, kind, length,
  reserved zero. Kind 1 is bounded JSON (512 KiB); kind 2 is 32-byte frame metadata
  plus bounded BGRA. JSON `frame` messages reference leased mapping slots. The
  initial `hello` authenticates token/version/transport; ACK includes slot and
  sequence. `bridge.c` and `LinuxBrowserProtocol.cs` define both ends.
* Three-second authentication/write deadlines, a 20-second unconnected startup
  deadline and a 12-second receive/heartbeat deadline bound failed sessions.
  Command and outgoing message queues are bounded to 64 entries. The plugin sends
  heartbeats while paused, cancels I/O on disposal and retries failures after
  ten seconds when the existing screen manager requests capture again.
* EOF, shutdown, SIGTERM and parent death stop the helper. Normal cleanup removes
  its endpoint/mapping/session directory and reaps its own WebKit children.
  SIGKILL or host crashes can leave a stale private directory; startup uses fresh
  unpredictable session IDs and does not reuse it. Automatic stale-directory
  garbage collection is not implemented.
* WebKit's sandbox remains enabled. Permissions, popups, downloads and external
  navigation schemes are blocked. Provider top-level navigation stays on its
  generated page. Generic Web is an untrusted user page, just as in WebView2.
  The JS shim is injected only in the top frame, and CrystalCast still validates
  screen nonces and telemetry bounds.
* IPC-approved Generic Web sources conservatively lock **all document navigation**
  to the approved initial URL until asynchronous native navigation approval is
  implemented. Redirects/iframes may therefore fail. This preserves the existing
  approval boundary rather than broadening it for the experiment.

## Reproduce independently on Linux

From `NativeLinuxBrowser`, after building:

```sh
mkdir -p artifacts
python3 tests/smoke.py --transport tcp --seconds 5
python3 tests/smoke.py --transport shared-file --seconds 5
python3 tests/lifecycle.py
```

These exercise actual rendered pixels, JavaScript execution/messages,
authentication failure, private permissions, three held frame leases, wrong ACK,
slow-consumer drops, abrupt disconnect, child cleanup, oversized packets and
unconnected startup timeout. Test logs remain in OS temporary directories for
failure investigation; runtime credentials are removed by normal helper cleanup.

Export the **actual plugin** scripts/pages on the Windows development machine:

```powershell
dotnet restore tools/CrystalCast.LinuxBrowserProbe -p:Platform=x64
dotnet build tools/CrystalCast.LinuxBrowserProbe -c Debug --no-restore -p:Platform=x64
dotnet run --project tools/CrystalCast.LinuxBrowserProbe -c Debug --no-build --no-restore -p:Platform=x64 -- --export-generic-script NativeLinuxBrowser/artifacts/generic-controller.js
dotnet run --project tools/CrystalCast.LinuxBrowserProbe -c Debug --no-build --no-restore -p:Platform=x64 -- --export-pages NativeLinuxBrowser/artifacts/providers --youtube-url "https://www.youtube.com/watch?v=GExciu1OHuE"
```

The existing ImageSharp audit failures can block restore; see
[the unchanged audit findings](../docs/DEPENDENCY_AUDIT.md). No dependency upgrade
or audit-policy suppression is part of this feature.

Copy exported files to the Linux checkout if it is separate. Generate a small
synthetic H.264/AAC fixture with system FFmpeg (a test tool, not a plugin runtime):

```sh
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=60 \
  -f lavfi -i sine=frequency=440:sample_rate=48000 -t 12 \
  -c:v libx264 -preset fast -crf 32 -g 60 -profile:v baseline -level 4.2 \
  -pix_fmt yuv420p -c:a aac \
  -movflags +frag_keyframe+empty_moov+default_base_moof \
  artifacts/test-1080-h264-aac-frag.mp4
python3 tests/video.py --fixture artifacts/test-1080-h264-aac-frag.mp4 \
  --width 1920 --height 1080 --video-width 1920 --seconds 12 --mse \
  --mse-codecs avc1.42c02a,mp4a.40.2 --transport shared-file \
  --output artifacts/1080-shared.json
# Repeat sequentially with --transport tcp and a different --output.
python3 tests/providers.py --pages artifacts/providers --seconds 30
```

The MSE fixture appends a small complete fragmented file; a much larger file can
exceed WebKit's SourceBuffer quota. This is a test fixture, not the streaming
implementation. `video.py` also tests progressive MP4 without `--mse`. Use a
matching `--video-width` assertion and codec string for a different fixture.

For a headless CI/container without an audio server, prefix video/provider tests
with `GST_PLUGIN_FEATURE_RANK=fakeaudiosink:1000`. This makes clocked decoding
testable but **does not test audible audio**. Do not use this setting for normal
desktop playback.

The `Containerfile` provides an Arch development environment. Mount the repository
at `/work`, build with `make`, and run tests as UID 1000. Nested WebKit bubblewrap
may need container seccomp adjustments (our Podman dev container used
`--security-opt seccomp=unconfined`); WebKit's own sandbox was not disabled. This
container is a development tool, not a proposed user distribution package.

`tests/serve_probe.py` starts a helper/fixture for a C# consumer. The Windows
`tests/run-wsl-probe.ps1` convenience script expects the development container
`crystalcast-wpe-dev` in Ubuntu WSL, and bridges the actual C# adapter to native
WPE over raw TCP. It privately copies the temporary endpoint, uploads received
pixels to a WARP D3D11 texture and asserts local pause/seek/rate behavior. Use
`-Provider YouTube -Url "https://www.youtube.com/watch?v=GExciu1OHuE" -Controls false`
to test the provider through that adapter. Its explicit dev launcher is separate
from the untested production Wine launcher.
Pass `-CheckBackend` to exercise the same readiness probe used by the setup UI,
including temporary helper cleanup, without loading a web page.

The convenience script expects this additional progressive fixture (generate
from `NativeLinuxBrowser` before invoking the script):

```sh
ffmpeg -i artifacts/test-1080-h264-aac-frag.mp4 -t 8 -vf scale=1280:720 \
  -c:v libx264 -preset fast -crf 28 -profile:v baseline -level 3.2 \
  -c:a aac -movflags +faststart artifacts/test-h264-aac.mp4
```

## Native game acceptance checklist

On each target distribution/Wine or Proton setup, record versions and diagnostics:

1. With WebView2 absent, install/run `--diagnose` as the desktop user, opt in and
   verify the helper launches without prefix edits. Confirm mapped frames or the
   explicit raw TCP fallback. Test missing libraries and missing helper errors.
2. Play the known YouTube sample. Verify advancing telemetry, moving video pixels,
   audio on/off/volume, pause/resume, seek, restart, rate and loop. Compare native
   overlay and experimental scene composite. Inspect foreground occlusion and
   camera motion separately from browser capture.
3. Test a **currently live, embeddable Twitch channel** for at least ten minutes,
   including pause/resume, live latency/reconnect and audio. Record the actual
   stream, time and errors; a ready embed or an offline player is not a pass.
4. Test known currently playable Dailymotion and Vimeo URLs, and Generic Web with
   accessible media. Exercise redirects, denied popups/permissions and the
   conservative IPC-approved navigation restriction.
5. Kill the helper/web process, disable/re-enable a screen, change provider,
   unload/reload the plugin and exit the game. Verify frame leases release,
   browser children terminate and private runtime files disappear. Confirm retry
   does not create overlapping helpers. Check a paused screen stays connected.
6. Benchmark one 1080p60 source for ten minutes before trying multiple screens.
   Record helper plus WebKit CPU, RSS, GPU engine utilization, decoded/dropped
   video frames, delivered frames, frame-age percentiles and D3D upload timing.
   Include game FPS/frametime and compare CPU TCP versus mapped frames. Then test
   a second screen and budget deferral; no multi-screen performance is promised.

This proof of concept is the default for new Linux configurations, but native
launch, audio, provider coverage and sustainable resource usage still need to
pass this checklist before the backend can be considered supported.
