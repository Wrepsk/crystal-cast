# WebKitGTK widget-frame investigation

Measured 2026-10-17. Experimental standalone prototype on
`codex/webkitgtk-frame-prototype`, based on `origin/master` at `9a97fde`.
The WPE commits are not ancestors of this branch. Windows WebView2 and the plugin
are unchanged. No browser-runtime packaging or engine builds were added.

**Finding:** continuous browser frames can be obtained through public GTK widget
rendering APIs without WebKit's asynchronous page snapshot API or desktop capture.
Actual H.264 playback and shared-memory BGRA delivery worked. A native GTK consumer
also accepted GSK's GPU-backed output textures. **Sustained 1080p60 delivery and
GPU export into Wine/DXVK were not established.** The working CPU route delivered
43–49 distinct frames/s; the mixed WSL GPU/readback route delivered about 9 frames/s.
These results do not characterize a native Linux desktop DMA-BUF pipeline.

The implementation, build instructions, flags and local benchmark commands are in
[`experiments/webkitgtk-frames`](../experiments/webkitgtk-frames/README.md).
Compact [recorded evidence](../experiments/webkitgtk-frames/results/2026-10-17/)
includes fourteen successful correctness replays, summaries, graphics logs,
environment details and hashes identifying the larger local raw records.

## What was prototyped

```text
Distro WebKitGTK WebView playing an actual 1920x1080 60 fps H.264 file
    -> mapped GTK4 widget / GtkWidgetPaintable invalidate-contents
    -> GdkPaintable snapshot into GtkSnapshot -> GskRenderNode
    -> exact full-viewport texture reference, when safe; otherwise GSK render_texture
       -> GdkTexture -> optional native GtkPicture consumer
       -> gdk_texture_download -> leased mmap BGRA -> independent full-frame consumer
```

`gdk_paintable_snapshot()` reads GTK's cached widget rendering; this is distinct
from requesting a new WebKit page screenshot. The probe subscribes to paintable
invalidation and schedules one bounded capture callback. GTK publishes the new
paintable image in a high-priority idle; matching that priority avoided capture
being delayed behind browser IPC. GTK's paintable documentation also explicitly
describes invalidation and the dependency on drawable widget state.
[GTK WidgetPaintable](https://docs.gtk.org/gtk4/class.WidgetPaintable.html),
[GTK 4.14 implementation](https://github.com/GNOME/gtk/blob/4.14.5/gtk/gtkwidgetpaintable.c).

GSK renders the complete node tree, preserving HTML/video composition. The guarded
direct path only accepts a single exact 1920x1080 texture, optionally inside a
nonrestrictive clip. It rejects transforms, scaling, opacity, multiple nodes and
overlays. **Every exercised browser node was a Cairo node**, so the direct texture
optimization was not exercised against a real browser DMA-BUF texture. Unknown
trees always take the complete GSK path; the probe never extracts an arbitrary
video texture while dropping page overlays.
[GSK render_texture](https://docs.gtk.org/gsk4/method.Renderer.render_texture.html).

The local server supports HTTP byte ranges. The fixture encodes a ten-bit frame
counter modulo 1024 and bottom-edge RGB reference bars inside the H.264 video.
The consumer copies all 8,294,400 bytes and checks frame IDs, BGRA channel order,
alpha and orientation. JavaScript separately reports playback time, intrinsic
video size and playback quality. Advancing JS or repeated identical captures alone
are insufficient to pass. The fixture has no audio; audible playback is untested.

There are three private mapped slots. A slot is never overwritten until its exact
sequence is acknowledged. Slow consumption drops new capture opportunities;
mapped-frame storage and the number of outstanding frames stay bounded. This is a
local Linux harness, not a new production transport or a Wine integration.

## GPU investigation and transfer boundary

The encouraging DMA-BUF observation is correct: WebKitGTK can carry compositor
buffers into GTK and append a `GdkTexture` to its widget snapshot. The reviewed
2.52.6 implementation builds `GdkDmabufTexture` from planes, offsets, strides,
fourcc and modifier; it also has an SHM/Cairo fallback. Thus GTK widget capture can
preserve a GPU texture instead of intrinsically requiring a browser screenshot.
That internal transport does not itself expose an application export handle.
[WebKitGTK accelerated backing store](https://github.com/WebKit/WebKit/blob/webkitgtk-2.52.6/Source/WebKit/UIProcess/gtk/AcceleratedBackingStore.cpp),
[WebKit DMA-BUF rendering history](https://bugs.webkit.org/show_bug.cgi?id=252988).

The public GTK 4.14 headers provide creation/building and lifetime APIs for
`GdkGLTexture` and `GdkDmabufTexture`, but no general getter for an existing
texture's GL name/context or DMA-BUF planes/fds/modifier. Builder getters expose
values the application supplied to a builder; they do not recover WebKit's
private builder. No private struct layout, symbol interception or engine patch
was used. The current upstream public headers were checked as well.
[GL texture header](https://github.com/GNOME/gtk/blob/4.14.5/gdk/gdkgltexture.h),
[DMA-BUF texture header](https://github.com/GNOME/gtk/blob/4.14.5/gdk/gdkdmabuftexture.h),
[upstream DMA-BUF texture header](https://github.com/GNOME/gtk/blob/main/gdk/gdkdmabuftexture.h).

| Boundary | Established here | Additional transfer/work |
|---|---|---|
| Widget -> GSK node | Continuous updates; snapshot costs tens of microseconds | Widget must remain drawable; browser rendering occurs upstream |
| GSK -> same-process GTK consumer | `GdkGLTexture` handed to a second `GtkPicture` | Texture reference/lifetime and rendering in GTK; no per-frame download required for that handoff |
| GSK -> external native GPU consumer | No exported GPU handle obtained | Need a supported exporter/access API and format/fence negotiation; a GPU blit into application-owned exportable storage may be necessary |
| GPU texture -> shared BGRA | Real readback measured, pixels verified | GPU synchronization/readback and possible format conversion through `gdk_texture_download` |
| CPU texture -> shared BGRA | Real mapped delivery and full consumer copy verified | GSK CPU rendering, texture download/copy into the slot, then consumer copy |
| Native helper -> Wine/DXVK game GPU | Not implemented or measured | Native fd import bridge, compatible device/image allocation, synchronization and D3D integration |

GTK documents texture download as potentially expensive and defines its format as
Cairo ARGB32: premultiplied BGRA bytes on the tested little-endian x86_64 host.
Rendering to a texture does not guarantee exportable allocation, absence of a
second pass, or completed GPU execution when the API returns. Texture-only timing
therefore measures submission/lifetime costs; sparse pixel audits are reported
separately. GL/NGL produced `GdkGLTexture`; Cairo produced `GdkMemoryTexture`.
[GTK download contract](https://docs.gtk.org/gdk4/method.Texture.download.html),
[GTK GL render-to-texture implementation](https://github.com/GNOME/gtk/blob/4.14.5/gsk/gl/gskglrenderer.c).

A DMA-BUF fd is neither a process-portable integer nor the DXGI shared handle
consumed by `CrystalCast/Rendering/SharedVideoTexture.cs`. A future bridge would
need real fd transfer (for example SCM_RIGHTS to a native Unix side), compatible
physical GPU/driver, supported format/modifier/plane layout, external-memory
capabilities, acquire/release synchronization, and lifetime acknowledgement.
Vulkan's fd import rules require compatible allocation/device identity; DRM image
layout needs the corresponding modifier support, beyond the memory extension.
[Vulkan fd import](https://docs.vulkan.org/refpages/latest/refpages/source/VkImportMemoryFdInfoKHR.html),
[DMA-BUF external memory](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_external_memory_dma_buf.html),
[DRM image modifiers](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_image_drm_format_modifier.html).

DXVK's current interop header has Vulkan device/image access and
`IDXGIVkInteropDevice1::CreateTexture2DFromVkImage`. That is an investigation target,
not an automatic bridge: it does not turn an opaque GTK object into a shared
image, and availability in the actual game runner, device features, ownership,
queue synchronization and Wine/native calling boundaries still need verification.
If imported storage cannot be wrapped directly, another GPU copy would be needed.
The existing BGRA consumer instead requires a later D3D11 upload, which this
benchmark excludes.
[DXVK interop definitions](https://github.com/doitsujin/dxvk/blob/master/src/dxgi/dxgi_interfaces.h).

## Measured 1080p60-input results

Ubuntu 24.04 distro WebKitGTK **2.52.6**, GTK **4.14.5**, Mesa **25.2.8**, on a
Ryzen 9 9955HX under WSL2. Runs were sequential, with five seconds of warmup and
approximately twenty seconds of measurement. The software matrix used Xvfb,
four llvmpipe threads, Skia CPU painting, CPU video sink and SHM compositor
transport, with WebKit compositing/sandboxing enabled. The GPU runs used WSLg
Wayland with an initialized **D3D12 AMD Radeon 610M GTK context**, then selected
software rendering for the WebKit child. Exact environment and provenance are
in [environment.txt](../experiments/webkitgtk-frames/results/2026-10-17/environment.txt).

| Pipeline | Actual output fps | Extraction mean / p95 | Helper-tree CPU | Peak summed RSS |
|---|---:|---:|---:|---:|
| Software GL, texture only, sparse audit | 25.67 | 13.08 / 19.80 ms | 584% | 838 MiB |
| Software GL -> shared BGRA | 16.09 | 40.76 / 53.71 ms | 391% | 827 MiB |
| Software NGL -> shared BGRA | 19.45 | 33.24 / 38.59 ms | 365% | 799 MiB |
| Cairo -> shared BGRA | 48.90 | 1.53 / 2.09 ms | 421% | 735 MiB |
| Cairo, 60 Hz polling -> shared BGRA | 48.94 | 1.45 / 1.97 ms | 415% | 736 MiB |
| Cairo -> shared BGRA, final repeat | 43.02 | 1.62 / 2.23 ms | 438% | 724 MiB |
| D3D12 GTK / software WebKit, texture only | 9.30 | 6.67 / 32.10 ms including audits | 137% | 799 MiB |
| D3D12 GTK / software WebKit -> shared BGRA | 9.34 | 32.23 / 38.94 ms | 153% | 797 MiB |
| D3D12 mixed -> shared BGRA, final repeat | 8.55 | 35.41 / 40.12 ms | 157% | 796 MiB |

Texture-only results are not external delivery rates and only sparse frames were
audited. The mixed GPU texture-only run's unaudited extraction averaged 4.90 ms.
The mixed GPU SHM runs spent **27.45–30.03 ms mean in download alone**. The first
capture explicitly logged the active GSK context as D3D12, establishing actual
GPU rendering in GTK, while the browser's node remained Cairo. The separate
preview run passed eight changing pixel audits and produced a diagnostic PNG;
its image/node serialization costs exclude it from the performance table.

The Cairo runs had no adjacent barcode repeats, but skipped source frames: mean
barcode step was 1.23–1.39 rather than 1. All produced frames reached the consumer
with matching IDs and valid reference bars. Consumer full-frame copy averaged
0.70–0.73 ms; extraction-start-to-consumer-copy age averaged 2.31–2.45 ms. This is
not decode-to-display latency. Polling did not establish 60 unique frames/s;
neither renderer labels nor video telemetry can substitute for pixel cadence.

The 16.67 ms 60 fps budget was exceeded by the measured GPU readback. Cairo's
extraction fit that budget, but the complete upstream widget/browser pipeline
did not sustain 60 fps in this environment. Repeats varied, so the 43–49 fps range
is more useful than selecting the best run. No performance advantage over another
engine, efficient in-game playback, hardware decode, GPU utilization or native
desktop compatibility follows from these WSL measurements.

CPU is approximate sampled helper-tree CPU (100% = one logical core), excluding
Python, Xvfb/WSLg and the game; short-lived or reparented processes can be missed.
Summed RSS can count shared pages multiple times. Extraction timings exclude JSON
output, final node release and optional diagnostics, whose costs still affect
observed throughput/CPU. Early timer placement allowed a small window overrun;
archived rates are normalized from actual monotonic start/end records. Final
repeats use a corrected start-relative deadline.

## Correctness, limitations and next experiment

Fourteen recorded runs passed the independent evidence replay. A separate overlay
check verified a cyan HTML rectangle above the moving video in all 255 delivered
frames, including a diagnostic PNG. Pause/resume
produced no captures during the settled paused interval and resumed with 307
distinct frame IDs. Hiding the widget stopped captured output entirely. A 100 ms
consumer delay produced 295 bounded drops, with all 82 published frames intact
and acknowledged. This confirms useful lease behavior, not a production IPC
security/lifecycle assessment.

Hiding matters: this approach currently needs a **mapped, drawable GTK widget**.
The hidden-widget check is a limitation for unattended embedding, not an offscreen
success. A private virtual display/compositor or another supported offscreen GTK
integration would need its own pacing, resource and launch tests. No PipeWire
desktop capture or permission portal was used. Audio-library PipeWire warnings in
some logs are unrelated to screen capture.

The unmodified sandboxed WebKit child failed EGL initialization when forced onto
WSL's D3D12 driver; it worked when the already-initialized GTK parent retained that
driver and the child selected software Mesa. WSL exposed `/dev/dxg`, not a DRM
render node, and the D3D12 EGL context advertised neither DMA-BUF import nor export.
This explains why the mixed test cannot validate WebKit's native DMA-BUF path.
The sandbox was not disabled to obtain a GPU result.

The next decisive test is this same small distro-linked probe on a real Linux
desktop with `/dev/dri`, running the normal driver selection, `texture`, `direct`,
`shm` and `render-shm` modes. Inspect whether WebKit contributes an exact
`GdkDmabufTexture`, audit changed pixels, and measure actual 60 fps cadence. In
parallel with that hardware test, establish a supported GPU handle/export route
and then a minimal Wine/DXVK import/fence proof on the same adapter. If public GTK
cannot expose the needed storage, evaluate upstream API work or an explicitly
measured copy into owned exportable storage before choosing a production backend.
Provider coverage, real FFXIV/Dalamud operation, audible audio, long sessions,
Steam/container launch integration and non-x86_64 hosts remain untested here.
