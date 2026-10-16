# Standalone WebKitGTK desktop benchmark

The shareable package reuses `experiments/webkitgtk-frames`; it is a hardware
validation harness, not a CrystalCast backend or a browser-runtime distribution.
Windows WebView2, WPE work and Wine prefixes are untouched.

Linux volunteers extract the x86_64 tar.gz/ZIP and run **`sh run-benchmark.sh`**.
They send **`summary.txt` and `results.json`** from the results folder. Optional
`--diagnostics` adds a report made from fixed rendering-error categories and
allowlisted observations. Nothing is uploaded or installed automatically.
Complete nontechnical instructions accompany the archive in
[READ-ME.txt](../experiments/webkitgtk-frames/READ-ME.txt).

The initial ABI target is Ubuntu 24.04: glibc 2.39+, GTK 4.14+, WebKitGTK API 6.0,
GStreamer MP4/H.264 decoding and Python 3.10+ standard library. No compiler,
headers, game, Dalamud, Wine or FFmpeg is required by the distributed runner.
Only the build environment uses FFmpeg to generate the included 20-second,
1920x1080 60fps H.264 fixture. The precompiled helper is about 44 KiB and the
complete compressed artifact about 17 MiB, dominated by the video. System browser,
media and graphics libraries are dynamically linked and never copied into it.

## Test contract

Four automatic passes use the normal desktop renderer selection, three seconds
of warmup **after playback becomes ready**, and twelve seconds of measurement.
A separate four-second overlay pass follows. Each startup has a 30-second limit;
missing/unsupported routes receive availability reasons rather than bogus zero-fps
performance results. Typical successful runtime is about 70 seconds.

`texture` measures GSK texture submission with sparse pixel audits. Strict
`direct` measures only an exact full-browser texture reference and reports
unavailable when a graph needs composition or scaling. `shm` retains that direct
texture when safe, otherwise renders the whole node; `render-shm` always renders
through GSK. Both SHM passes download directly into leased slots and copy every
frame into the independent consumer. They provide the fully audited counterparts
to texture/direct submission measurements.

Pixel validation preserves the encoded ten-bit frame IDs, RGB bars, orientation
and alpha checks. It adds matching producer/consumer FNV-1a samples across the
whole frame to detect torn/corrupted slot delivery. The overlay pass independently
checks cyan HTML above the video. No screenshot or PNG write occurs in timed
passes. The video is served through an ephemeral loopback-only HTTP server with
byte-range support; no internet provider influences the primary benchmark.

The small mapped window contains a full-size 1920x1080 WebView inside a clipped
desktop viewport. The capture observes the WebView, and verifies its actual
allocation and rendered pixels at full resolution. The window must remain mapped;
hiding/minimizing it can suppress GTK updates. The test is not a headless solution.

Reports distinguish texture-submission cadence, fully verified unique delivered
fps, repeated IDs, skipped input frames, browser-reported drops and explicit
backpressure drops. A 10-bit counter wraps modulo 1024; the default measurement
duration fits within that interval.
Counter resets/reordering are reported separately from source skips. Unique
delivery counts use unwrapped IDs across the window, including nonadjacent repeats.
Discontinuous or ambiguous sequences receive unknown skip totals rather than
invented modulo-sized drops.
Reported extraction/download/copy times include
mean and p95. Output `GdkGLTexture` / `GdkMemoryTexture` types are separate from
browser-node `GdkDmabufTexture`, GL, memory or Cairo observations. Unknown node
types are counted, not silently treated as proof that DMA-BUF is absent.

"Sustained verified 1080p60" uses an explicit near-60 tolerance: >=59 independently
verified unique fps, >=57 frames in every complete one-second bucket, <=2% skipped
input frames, >=10 measured seconds, a completed requested window, no counter
resets/reordering/ambiguity, and all integrity checks passing. Sparse
texture audits can never satisfy that claim. This says neither that every input
frame arrived nor that a game would present it efficiently.

## Environment and privacy

The structured report allowlists distro ID/version, session type, numeric kernel
version, architecture, logical CPU count, DRM GPU vendor/device IDs and driver
name, optional NVIDIA model/driver query, runtime versions, codec-factory presence
and rendering observations. It records known rendering overrides without recording
arbitrary environment variables. VM detection uses known WSL/DMI markers and
reports its heuristic limits; it does not preserve raw DMI data.

GL renderer/version strings provide Mesa/NVIDIA details where observable. A
capability-probe context is not mistaken for the renderer doing the test: Cairo
is classified as CPU even when GL hardware is available; unobservable active
Vulkan/GL implementations remain unverified. CPU-visible browser output with an
observed hardware GTK context is labeled mixed. Hardware decode remains unverified;
codec factories and DMA-BUF storage alone do not prove its use.

Both helper-tree and complete runner/consumer/browser CPU/RSS estimates are
reported, excluding the desktop compositor. Short-lived or reparented processes
can be missed; summed RSS can double-count shared pages and does not measure VRAM.
Readback is distinguished from a CPU texture download/copy. No game upload or
decode-to-game-display latency is measured.

Reports omit usernames, home/package paths, addresses, hostnames, machine IDs,
GPU UUIDs/serials, complete process lists and arbitrary browser logs. Private raw
records and temporary mappings live in an automatically removed temporary folder.
Known error signatures become fixed categories; unknown raw error text is discarded.
There is no automatic upload, telemetry endpoint or permission portal.

## Distribution and compatibility

[GitHub Actions](../.github/workflows/webkitgtk-benchmark.yml) builds on Ubuntu
24.04, runs reporting/integrity tests and tests the extracted artifact in a clean
runtime-only Ubuntu container before uploading archives and SHA-256 checksums.
Optional manual publication produces a separate immutable benchmark prerelease,
preserving the latest Windows plugin release. Source and AGPL license accompany
the executable. This workflow does not build an engine, install a game, or package
a transitive browser-runtime library closure.

On Ubuntu's GitHub-hosted runner, a container-specific AppArmor profile explicitly
allows the nested user namespace required by the browser sandbox. It is removed
after the test. Global kernel restrictions and WebKit sandboxing are unchanged;
this CI-only setup is not part of the volunteer package or launcher.
Docker's container-only proc masks are also removed so the nested sandbox can
mount its own procfs; no privileged container or host PID namespace is used.
[Ubuntu namespace policy](https://documentation.ubuntu.com/release-notes/24.04/),
[Docker container profiles](https://docs.docker.com/engine/security/apparmor/).

The same locally Ubuntu-built executable also passed runtime/ABI checks in Fedora
44 (GTK 4.22.5, WebKitGTK 2.54.1, GStreamer 1.28.8) and Arch (GTK 4.24.1,
WebKitGTK 2.54.1, GStreamer 1.28.8) containers. The tested Fedora repository set
had no H.264 decoder factory; Arch had both libav and OpenH264 factories. These
are loadability/capability checks, not native desktop playback or GPU measurements.

The Ubuntu runtime package names include `libwebkitgtk-6.0-4` and GStreamer codec
plugins. Fedora uses `webkitgtk6.0`; Arch uses `webkitgtk-6.0`. These are guidance,
not evidence of codec availability or native GPU playback on every distro.
[Ubuntu package](https://packages.ubuntu.com/noble/libwebkitgtk-6.0-4),
[Fedora package](https://packages.fedoraproject.org/pkgs/webkitgtk/webkitgtk6.0/),
[Arch package](https://archlinux.org/packages/extra/x86_64/webkitgtk-6.0/).

SteamOS users should use Desktop Mode and send the unavailable report if its
runtime libraries do not satisfy the checks. Do not unlock its read-only system,
replace libraries or infer compatibility solely from its Arch ancestry. Older
glibc/GTK hosts, musl and ARM64 are outside this artifact's target. There is no
Flatpak/Steam runtime integration or second distro-specific build project here.

Native results establish widget capture cadence, observed texture/storage paths,
readback and CPU-copy costs on the tested system. DMA-BUF presence does not expose
a DXGI shared handle or establish safe zero-copy export. Wine/DXVK import, adapter
identity, buffer/fence ownership, game upload/presentation, audible audio, long
sessions and actual FFXIV/Dalamud performance remain separate integration tests.
