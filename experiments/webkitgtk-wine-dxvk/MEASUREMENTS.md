# Measured 2026-10-17: standalone Wine/DXVK bridge

The existing native WebKitGTK probe and published native benchmark were not
changed. This adds an unmanaged Windows consumer and Linux coordinator only.

## Successful local configuration

Ubuntu 24.04.5 container on WSL2, x86_64; Wine 9.0 (Ubuntu build), DXVK 2.7.1
unmodified upstream x64 DLLs, Mesa 25.2.8 software Vulkan device reported as
`llvmpipe (LLVM 20.1.2, 256 bits)`. GTK 4.14.5 / WebKitGTK 2.52.6; native GSK
Cairo output (`GdkMemoryTexture`), no browser DMA-BUF textures in this test.
Both native WebKit rendering and Vulkan were software, not native GPU hardware.

The fixture was extracted from the published 0.1.0 native benchmark, SHA-256
`6b21f599ac02aebc6092026c10c1a1f93a4db6cb411871235f475de27187fcfb`.
DXVK was downloaded for this developer test from
[upstream 2.7.1](https://github.com/doitsujin/dxvk/releases/tag/v2.7.1);
archive SHA-256 `d85ce7c79f57ecd765aaa1b9e7007cb875e6fde9f6d331df799bce73d513ce87`.
Neither Wine nor DXVK is bundled or installed by the harness.

Each pass measured 12 seconds after a 3-second native video warmup. The final
recorded run delivered 720 unique frames per pass, without repeated/skipped source
frames or capture backpressure drops. There were zero producer/consumer hash or
marker mismatches, invalid reference bars, or GPU pixel mismatches. DXVK version
and adapter were observed, and both processes exited successfully.

| Final recorded run | Upload + sparse GPU audits | Full GPU verification |
|---|---:|---:|
| Source-verified unique frames/s | 60.00 | 60.00 |
| Complete-path independently verified frames/s | Not established | 60.00 |
| Native extraction mean / p95 | 1.29 / 1.57 ms | 1.32 / 1.60 ms |
| UpdateSubresource CPU call mean / p95 | 0.54 / 0.69 ms | 0.55 / 0.72 ms |
| Backbuffer copy + GPU completion wait mean / p95 | 1.33 / 1.68 ms | 1.60 / 2.24 ms |
| Additional GPU readback + byte validation mean / p95 | 1.28 / 1.53 ms, sparse | 1.66 / 2.09 ms, every frame |
| Capture start to Present acknowledgement mean / p95 | 4.62 / 5.23 ms | 6.74 / 7.65 ms |
| Observed total tree CPU, logical cores | 3.67 | 3.99 |
| Peak summed tree RSS | 980 MiB | 978 MiB |

The full verification pass passed the stated sustained 1080p60 tolerance,
including both capture-time and delivery-arrival one-second buckets. This is
one short software run, not evidence of low-power or efficient game operation.
An earlier full verification run delivered about 51.65 unique frames/s; timings
vary with host load and warm state. First-use compilation/driver costs are
included, and occasional individual latencies exceeded one 16.67 ms frame period.
The separate four-second HTML-over-video pass also verifies every byte after
upload; its report is recorded alongside the baseline. A four-second correctness
test cannot establish sustained 1080p60 under the >=10-second rule.

## Reproduction controls used only in the local developer test

The container has the existing distro WebKitGTK development environment plus
MinGW, Wine64, Mesa Vulkan drivers and Vulkan tools. A native Linux-filesystem
dedicated prefix was used. Test commands were run as uid 1000 with the normal
WebKit sandbox enabled. No game prefix was used.

The outer test process explicitly selected:

```sh
GTK_A11Y=none GDK_BACKEND=x11 GSK_RENDERER=cairo \
LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=4 \
WEBKIT_SKIA_ENABLE_CPU_RENDERING=1 WEBKIT_GST_DISABLE_GL_SINK=1 \
dbus-run-session -- xvfb-run -a \
  -s '-screen 0 1920x1080x24 -extension RANDR' \
  python3 run.py --dxvk-dir /path/to/dxvk-2.7.1/x64 \
  --wine-prefix /path/on/linux/filesystem/test-prefix \
  --native-binary ../webkitgtk-frames/build/gtk-frame-probe \
  --fixture /path/to/published-benchmark/data/video.mp4 \
  --seconds 12 --warmup 3 --mode both --output /path/to/empty/results
```

These are reproducibility controls, not launcher defaults or suggestions for
native desktop testers. Do not copy software-renderer overrides into a hardware
test. The native producer remains a mapped/drawable GTK widget. The backbuffer
allocation and validation remain 1920x1080 even though the desktop windows are
smaller.

## Failures retained as limitations

With Xvfb's default RandR extension, both tested DXVK 2.4.1 and 2.7.1 configurations
raised `0xc0000094` (integer divide by zero) during `Present` in `dxgi.dll`. Wine 9
and the existing Wine-XIV 10.8 runner both encountered it. The exception occurs
after the upload, event-query completion and full backbuffer readback. These runs
were correctly rejected. The Xvfb test server's zero-refresh display metadata is
the suspected trigger; removing RandR from that disposable server resolved it.
No Wine/DXVK code was patched, and the harness changes no desktop display settings.

A prefix on the workspace's Windows/DrvFS filesystem failed Wine server startup;
the successful prefix was on Linux storage. WSLg's display was unavailable in
this session. No work was done to enable WSL GPU acceleration.

## What remains

Native desktop Wine/DXVK measurements on the Fedora tester's hardware are the next
check. The Fedora native-only report already observed browser DMA-BUF textures;
this experiment has not yet tested that GPU-backed browser -> BGRA -> Wine upload
combination. It also does not exercise a managed CLR host, CrystalCast's actual
device/frame consumer, the game, audio or long sessions. No DMA-BUF handle import,
GPU cross-process synchronization, zero-copy route, Proton host-launch integration
or game performance is established here.
