# WebKitGTK widget-frame experiment

For Linux volunteers, use the **precompiled standalone benchmark package** and
[READ-ME.txt](READ-ME.txt). Extract it and run `sh run-benchmark.sh`; no repository
checkout, compiler, FFmpeg, game or Wine is required. The launcher uses system
runtime libraries and Python 3.10+, checks availability, and saves only local
privacy-conscious `summary.txt` / `results.json` reports.

The standalone runner reuses `benchmark.py`, the encoded H.264 fixture generator,
the native `probe.c` capture path and independent mmap consumer below. It does not
select software drivers, change system packages, or disable sandboxing. Its small
mapped viewport contains a full 1920x1080 WebView; frame dimensions and independent
pixel checks verify that capture has not silently become a 960x540 test.

The package runs `texture`, strict `direct`, `shm`, `render-shm` and a separate
short overlay check. Warmup begins after playback readiness. Texture-only passes
have sparse audits and report submission cadence; fully audited SHM companions
report unique delivery, repeats/skips, sampled integrity hashes and readback/copy
costs. A direct path that needs additional rendering is reported unavailable.
Source DMA-BUF textures are inspected separately from GSK's output texture.

## Maintainer package build

On Ubuntu 24.04, install the developer prerequisites below plus
`libgstreamer1.0-dev` and Git, then:

```sh
python3 -m unittest -v test_reports.py
python3 build_package.py --version 0.1.0
bash smoke_package.sh artifacts/package/*.tar.gz
```

The build makes a small dynamic x86_64 executable and a 20-second copy of the same
counter/RGB-bar fixture recipe. It packages source/license, executable, Python
stdlib runner and video into tar.gz and ZIP with SHA-256 manifests. No shared
libraries are copied. `smoke_package.sh` is a **maintainer/CI-only** disposable
Ubuntu runtime-container test; it checks actual playback with no compiler,
headers, pkg-config or FFmpeg present, plus a clean unavailable-desktop report.
It does not change the host's runtime packages.

[The GitHub Actions workflow](../../.github/workflows/webkitgtk-benchmark.yml)
builds/tests on Ubuntu 24.04 and uploads the archives. After the workflow is in the
default branch, manual dispatch can optionally publish an immutable separate
`webkitgtk-benchmark-vX.Y.Z` prerelease. Existing versions are never replaced;
benchmark prereleases are never selected as the latest plugin release. Plugin
release tags remain numeric and their workflow ignores benchmark tags.

Ubuntu 24.04 is the initial tested ABI target. Fedora and Arch runtime names are
documented, and missing libraries/codecs produce availability reports; desktop GPU
operation on those systems and SteamOS requires volunteer measurements.

The remaining sections describe the original developer investigation and its
WSL-specific controls; they are not required by volunteer testers.

Developer-only standalone probe, based on `origin/master` at `9a97fde`. No plugin
backend, configuration, Windows capture code, WPE runtime, or packaging changes.
Uses distro-installed **WebKitGTK API 6.0** and **GTK >= 4.14**. The API namespace
is not the WebKit engine's version number.

Read [the investigation and measured results](../../docs/WEBKITGTK_FRAME_CAPTURE.md).

## Build and run on a Linux development desktop

Ubuntu 24.04 prerequisites (developer machine only):

```sh
sudo apt-get install build-essential pkg-config libwebkitgtk-6.0-dev libgtk-4-dev \
  libepoxy-dev libgstreamer1.0-dev gstreamer1.0-libav gstreamer1.0-plugins-good \
  gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-gl \
  ffmpeg python3 dbus-x11 xvfb xauth mesa-utils
cd experiments/webkitgtk-frames
make
# Keep the normal desktop GPU/driver selection for the first experiment.
python3 benchmark.py --mode texture --seconds 20 --output artifacts/desktop-texture
python3 benchmark.py --mode direct --seconds 20 --output artifacts/desktop-direct
python3 benchmark.py --mode shm --seconds 20 --output artifacts/desktop-shm
python3 benchmark.py --mode render-shm --seconds 20 --output artifacts/desktop-render-shm
# Diagnostic native texture consumer; includes extra readbacks/writes.
python3 benchmark.py --mode texture --preview --png --audit-every 10 \
  --output artifacts/desktop-preview
```

Run as an ordinary user with a functioning display and WebKit sandbox. The
benchmark creates a loopback HTTP server, ephemeral browser profile, 45-second
1920x1080 60 fps H.264 fixture (no audio), and private temporary mapping. The fixture
contains a ten-bit frame number modulo 1024 and RGB reference bars **encoded in
the video**, not drawn by JavaScript. HTTP range requests are supported.

`texture` always calls `gsk_renderer_render_texture`; `direct` can instead retain
an exact full-viewport `GSK_TEXTURE_NODE`'s texture. `shm` uses the same guarded
direct path, falling back to GSK; `render-shm` always renders through GSK.
Transforms, overlays, multiple nodes and nontrivial clips cannot take the direct
path. A node without an exact texture is reported as `composite`; inspect the
diagnostic tree to distinguish Cairo nodes from a composite of GPU textures.

Texture modes only download on `--audit-every N` (default 60); use 0 to disable
pixel audits. Their unaudited times measure API submission/texture lifetime, **not
GPU completion or external delivery**. `--preview` hands the texture to a second
same-process `GtkPicture`, which holds a reference until replacement. That is a
native GTK consumer, not GPU IPC or Wine interoperability. SHM modes download
every frame directly into one of three leased BGRA slots; Python copies the full
frame, checks the barcode/channel bars, and acknowledges the sequence before reuse.
The small `SOCK_SEQPACKET` protocol is only a local Linux test harness.

Capture normally follows `invalidate-contents`, with one pending high-priority
idle callback. `--poll-hz 60` polls the cached paintable to investigate pacing;
repeated pixels are counted separately. It does not force WebKit to compose at
60 fps. `--exercise-pause` pauses at 8 seconds after page initialization and resumes
at 10 seconds. `--hide-after N` tests hidden-widget behavior after N measured
seconds. `--consumer-delay-ms 100` tests leased-slot backpressure. `--overlay`
adds static HTML above the video to exercise whole-page composition.

`--png` serializes the first render node and writes a PNG. Those operations can
force readback/encoding and disturb subsequent timing, so use a separate diagnostic
run. Ordinary benchmarks do neither. All native sample durations exclude JSON
logging, render-node release, and optional diagnostics; observed FPS and tree CPU
include those costs. The readback and texture release are timed separately.

Each run writes `summary.json`, `events.jsonl`, `consumer.jsonl`, and `stderr.log`.
Failures retain diagnostics. Success establishes advancing media, changing audited
pixels, correct BGRA reference bars and matching producer/consumer barcodes where
applicable; **it does not assert 60 fps**. Inspect the summaries for actual cadence,
missed/repeated source frames, backpressure, and media errors.

Replay recorded correctness independently with `python3 verify_results.py` for
the matrix, or pass individual artifact directories. `record_evidence.py` copies
compact summaries/logs into a chosen results directory, records raw-file hashes,
and normalizes throughput against actual monotonic measurement windows.

## Reproduce the software comparison

```sh
python3 run_matrix.py
```

This explicitly selects Xvfb, software Mesa, four llvmpipe threads, Skia CPU
painting, the CPU video sink, and WebKit's SHM compositor transport. It leaves
WebKit compositing and sandboxing enabled. It compares GTK GL, NGL and Cairo, then
checks 60 Hz polling, pause/resume, hiding and a slow consumer sequentially. It
does not characterize a hardware DMA-BUF path. Do not use these environment
overrides as production defaults.

The recorded investigation used an Ubuntu 24.04 Podman development container on
WSL2. The only container seccomp override allowed WebKit's nested sandbox to run;
WebKit's own sandbox remained enabled. No browser libraries were built, bundled
or distributed. Runtime files and generated fixtures stay under ignored
`artifacts/`; build output stays under ignored `build/`.

The WSL diagnostic `--software-web-process` selects software Mesa for children
**after** the native window/GSK context has initialized. With the parent started
using `MESA_LOADER_DRIVER_OVERRIDE=d3d12 GALLIUM_DRIVER=d3d12`, this permits a mixed
GTK D3D12 / WebKit CPU experiment without disabling WebKit's sandbox. The first
capture logs the current GSK GL renderer, verifying which context actually
rendered the output. This is a development-only workaround for WSL's unavailable
DRM/DMA-BUF path, not a production launcher or an all-GPU browser benchmark.
