# Native GTK to Wine/DXVK frame test

## Precompiled release: start here

Download and extract the tar.gz or ZIP from the separate
[Wine/DXVK test prerelease](https://github.com/Wrepsk/crystal-cast/releases/tag/webkitgtk-wine-dxvk-v0.1.0),
open a terminal inside its folder, and run:

```sh
sh run-wine-dxvk.sh
```

Send `wine-dxvk-results/summary.txt` and `wine-dxvk-results/results.json`.
The single download includes the programs, video and unmodified DXVK 2.7.1 x64
DLLs with their license. It uses your existing Wine and system browser/media
libraries. No compiling, second benchmark download, Wine/DXVK installation,
Winetricks or game is required. If Wine is not found, pass
`--wine "/full/path/to/wine64"`. For a report without opening windows or creating
a Wine prefix, run `sh run-wine-dxvk.sh --check --output dependency-check`.
See **READ-ME.txt** for tester instructions, requirements and the optional overlay
check. Only the native helper was built by GitHub Actions; this release's Windows
consumer was compiled and tested in the Ubuntu 24.04 developer container.

The remaining sections document the integration and developer/add-on usage.

Small standalone integration experiment. The published native benchmark and
CrystalCast plugin are unchanged. No game, Dalamud, .NET, browser runtime bundle,
DMA-BUF export implementation or GPU-sharing backend is involved.

The existing WebKitGTK probe plays the same local 1920x1080 60fps encoded-counter
H.264 fixture and downloads its GSK output into three leased BGRA file slots.
An authenticated loopback coordinator relays only slot metadata to an x64 Windows
C executable under Wine. `CreateFileMapping` / `MapViewOfFile` opens the same file
through Wine's ordinary `Z:` mapping. No video pixels traverse TCP. The executable
independently checks source counters, sampled hashes, channel/orientation bars and
optional overlay pixels, calls `UpdateSubresource` on a default D3D11 texture,
copies it to a full-resolution backbuffer, waits for GPU completion with an event
query, and calls `Present(0)` in a small 960x540 client window.

Two passes keep correctness costs visible:

* `upload`: verifies every source frame, waits for every upload/backbuffer copy to
  complete, and audits backbuffer pixels on the first and every 60th frame. Its
  source-verified FPS is **not** fully GPU-verified delivery FPS.
* `verify`: additionally copies every backbuffer to a staging texture, maps it,
  verifies its counter/bars/overlay, and compares **every BGRA byte** against the
  still-leased source slot, respecting staging row pitch. Only this pass can claim
  complete-path verified delivery. This extra GPU-to-CPU readback is diagnostic
  overhead; a future game consumer would not normally perform it.

The coordinator releases a slot only after the consumer acknowledges completion.
It checks sequence identity and compares Windows source counters/hashes against
the native producer independently. Missing deliveries, corruption, unsuccessful
Present, native/Windows errors, or absent DXVK version evidence invalidate a pass.
WineD3D is not accepted as DXVK. Native-only DLL overrides and DXVK log evidence
identify the D3D11 implementation actually used; logs stay in temporary storage.

## Run on a Linux desktop

Use an existing Wine runner and an existing DXVK release's **x64** DLL directory.
This harness neither downloads them nor installs packages. Normal system
WebKitGTK/GTK/GStreamer requirements from the native benchmark still apply.
Wine's `Z:` Unix-filesystem drive must be enabled. This launcher directly invokes
a Wine executable; Proton's Steam/container launcher integration is not tested.

Use the previously published native benchmark folder for its precompiled
`bin/gtk-frame-probe` and `data/video.mp4`. From this harness directory:

```sh
python3 run.py \
  --wine /absolute/path/to/wine64 \
  --dxvk-dir /absolute/path/to/dxvk/x64 \
  --native-binary /absolute/path/to/native-benchmark/bin/gtk-frame-probe \
  --fixture /absolute/path/to/native-benchmark/data/video.mp4 \
  --output wine-dxvk-results
```

Run as your ordinary desktop user. Keep both windows visible and unminimized.
Default: 3-second native video warmup, then 12 seconds per pass. Initial Wine
prefix setup can take longer. The browser page waits for Windows initialization
before playback starts. First-use D3D11/driver work remains included in timing.

The harness uses its own marked prefix, by default
`~/.cache/crystalcast-frame-harness/wine-prefix` (or under `XDG_CACHE_HOME`). An
unmarked existing prefix is refused, so it cannot accidentally use a game prefix.
Use a Linux filesystem for the prefix; Wine prefixes on NTFS/DrvFS did not work in
the local test. You can select a new dedicated location with `--wine-prefix`.
Concurrent harnesses cannot use the same prefix. DXVK DLLs are copied beside a
temporary copy of the test executable, never into an existing application's
prefix. Its matching sibling `wineserver`, when available, is stopped and awaited
for this dedicated prefix on exit. No system Wine settings are changed.

For the additional HTML-over-video correctness test, use another empty output
directory:

```sh
python3 run.py --wine /absolute/path/to/wine64 \
  --dxvk-dir /absolute/path/to/dxvk/x64 \
  --native-binary /absolute/path/to/native-benchmark/bin/gtk-frame-probe \
  --fixture /absolute/path/to/native-benchmark/data/video.mp4 \
  --mode verify --overlay --seconds 4 --warmup 1 --output wine-overlay-results
```

Send `summary.txt` and `results.json` from each output directory after reviewing
them. The reports retain allowlisted graphics/runtime information and fixed error
categories, not raw browser/Wine logs, usernames, machine IDs or home paths.
Everything stays local. Existing reports are never overwritten.

## Build the Windows consumer (maintainer only)

On a Linux development machine with the MinGW x64 C cross-compiler:

```sh
make
python3 -m unittest -v test_harness.py
```

The Windows executable is `build/wine-frame-consumer.exe`; it needs no .NET or
MinGW runtime installation. Testers can use a precompiled copy. `run.py` imports
the existing prototype's `benchmark.py` and `run_benchmark.py` from its sibling
directory. A small shareable add-on can instead include those unchanged files
under `native/`, alongside `run.py`, this README, the license, consumer source and
`build/wine-frame-consumer.exe`. It reuses the original native package's executable
and video and expects an external DXVK directory. The self-contained release
instead includes the unchanged native package and the two tested upstream DXVK
DLLs, with the license and source URL. Neither form contains browser libraries
or Wine. No additional browser packaging or release workflow is involved.

`build_release.py` assembles that release from the reviewed native 0.1.0 tarball,
DXVK 2.7.1 tarball and exact license, checking pinned input hashes. It performs no
downloads or compilation. After `make` and the tests:

```sh
python3 -m unittest -v test_harness.py test_launch.py
python3 build_release.py --native-archive /path/to/native-benchmark.tar.gz \
  --dxvk-archive /path/to/dxvk-2.7.1.tar.gz --dxvk-license /path/to/DXVK-LICENSE
```

## Interpret the results

`update_api_us` measures the CPU call only. It is not completed GPU upload time:
Microsoft documents that UpdateSubresource can queue an asynchronous GPU copy.
`gpu_copy_completion_us` includes backbuffer-copy submission, flushing, and event
query completion after that call. It includes synchronization/scheduling and is
not an isolated GPU timestamp. `gpu_readback_validation_us` separately includes
staging copy, blocking Map, and full-byte validation, only on audited frames.
`capture_start_to_present_ack_us` includes native extraction, control relay,
Windows work, optional readback, Present and response receipt. It uses two Linux
monotonic timestamps; Windows QPC intervals are never subtracted from Linux time.

Unique-frame FPS includes final response draining after native capture ends.
Source and delivery one-second buckets are recorded separately. The 60fps claim
requires all frames verified after upload, >=59 unique delivered fps, >=57 in
every complete one-second source **and arrival** bucket, <=2% skipped input,
>=10 seconds, complete measurement, pixel integrity, and no counter ambiguity or
reordering/reset. This is a stated near-60 tolerance. Present completion is not
proof of physical scanout or end-to-end display latency.

CPU/RSS sampling covers observed runner descendants; it can miss detached Wine
servers, excludes the desktop compositor, and summed RSS double-counts sharing.
A software Vulkan adapter is explicitly labeled. A hardware adapter name does
not independently establish hardware browser rendering or video decoding.

The route performs a native BGRA download/copy and a D3D11 upload, plus a GPU
backbuffer copy. It does **not** export GTK DMA-BUF textures or remove these
transfers. The verification pass adds another readback. A successful result
establishes this standalone unmanaged Wine/DXVK bridge on that environment;
CrystalCast's managed host, game-device resource sharing, in-game scheduling,
audio, long sessions and real desktop GPU performance remain separate tests.

Reference semantics:
[UpdateSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource),
[D3D11 event queries](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query),
[DXVK](https://github.com/doitsujin/dxvk).

## Local software-only test

See `results/2026-10-17/` for compact captured reports and `MEASUREMENTS.md` for
the tested configuration and limitations. No code in the harness selects the
software renderer or disables RandR. In the disposable WSL2 test container only,
Xvfb's default RandR setup triggered an integer divide by zero in DXVK Present
(2.4.1 and 2.7.1); zero-refresh metadata is the suspected cause. The test server
was started with `-extension RANDR`;
normal desktop testers should use their normal display configuration.
