# Optional Linux browser runtime distribution

Standalone candidate built and tested, 2026-10-16. The native backend remains experimental. A runtime
candidate must pass the checks below before its hashes are added to the plugin's
download catalog. An empty catalog intentionally disables automatic installation.

## Strategy

Build WPE WebKit 2.54.1 and the native helper on Ubuntu 24.04 x86-64, then distribute
a separate, versioned ZIP containing its runtime library closure, subprocesses,
GStreamer plugins, fonts/MIME data, sandbox tools and license notices. CrystalCast
downloads this optional package to `$XDG_DATA_HOME/crystalcast/browser` (or
`$HOME/.local/share/crystalcast/browser`). The Windows plugin ZIP remains separate.

Ubuntu 24.04 is an initial build candidate, not a claim of universal portability.
It supplies glibc 2.39 and a supported WebKit build baseline. Bundling the engine's
non-glibc libraries avoids differences in GLib, ICU, GStreamer and C++ library
versions. The host glibc and loader remain together; ELF symbol requirements are
checked during packaging. The host's normal CA trust store is required for HTTPS;
the package does not override trusted certificate authorities or disable TLS
verification. Debian 12's older libc is outside this candidate's
declared baseline. Debian 13 and newer systems may satisfy the ABI, but still need
execution tests. Older SteamOS images cannot be assumed to have glibc 2.39.
[WebKit dependencies policy](https://docs.webkit.org/Ports/WebKitGTK%20and%20WPE%20WebKit/DependenciesPolicy.html),
[Ubuntu 24.04 toolchain](https://documentation.ubuntu.com/release-notes/24.04/).

Do not fix an ABI failure by replacing the user's system libc. If coverage of
older hosts is required, evaluate a separately tested older-baseline build with
backported build dependencies, or a supported container runtime. Do not add an
unverified second artifact merely to label those systems supported.

| Format | Assessment for this integration |
|---|---|
| Versioned ZIP directory | First candidate. Windows .NET can verify/extract it through Wine, without FUSE, root or a package manager. Executable modes are restored using a bounded native chmod invocation. Requires explicit runtime relocation and sandbox testing. |
| AppImage | Does not solve libc/GPU ABI or WebKit sandbox constraints. FUSE availability and mounted-image paths add launcher complications; extracting the AppDir would largely recreate the chosen directory format. |
| Flatpak | A useful potential host-managed runtime/sandbox route, especially on immutable systems. It introduces portal/host-launch and shared-file/network namespace integration, requires Flatpak availability, and cannot be assumed to run inside XIVLauncher/Proton identically. Needs a separate vertical slice before selection. |
| Per-distro deb/rpm packages | Can integrate system policy/dependencies, but user installation would commonly require a package manager/admin access. Useful for maintainers or a fallback distribution route, not the first terminal-free user flow. |

## Build and relocation

Developer/CI build commands (Ubuntu 24.04 only; end users download the package):

```sh
sudo apt-get update
sudo xargs -r apt-get install -y --no-install-recommends < NativeLinuxBrowser/packaging/ubuntu24-build-dependencies.txt
bash NativeLinuxBrowser/packaging/build_wpe.sh
python3 NativeLinuxBrowser/packaging/package_runtime.py --version 0.1.0 --output NativeLinuxBrowser/artifacts/release
# Enable matching deb-src indices on the build host before collecting sources.
python3 NativeLinuxBrowser/packaging/collect_sources.py --runtime /var/tmp/crystalcast-runtime-build/runtime-0.1.0 --output NativeLinuxBrowser/artifacts/sources --bundle-output NativeLinuxBrowser/artifacts/source-release
```

`NativeLinuxBrowser/packaging/build_wpe.sh` downloads the exact upstream source
archive, verifies its published SHA-256 and applies a narrowly scoped path patch.
It builds Release WPE without developer mode. Wayland, DRM/headless platform,
media and bubblewrap sandbox support remain enabled. Documentation, introspection,
spell checking, speech synthesis, WebDriver and optional backtrace instrumentation
are omitted; playback/rendering features are not removed to simplify packaging.
[WPE 2.54.1 source/hash](https://wpewebkit.org/release/wpewebkit-2.54.1.html).

The upstream release embeds absolute child-process and sandbox-tool paths.
`patch_wpe_paths.py` changes only those lookups and adds a read-only mount of the
runtime tree inside WebKit's existing sandbox. It deliberately does not enable
WebKit developer mode or turn sandboxing off. The exact patch recipe accompanies
corresponding source. Upstream changes fail the patch's expected-content checks
and require review rather than silently applying fuzzy edits.

The static native bootstrap derives its installation directory from
`/proc/self/exe`, checks ownership/write permissions, clears inherited dynamic
loader/plugin overrides, and configures paths to bundled libraries, WebKit child
processes, the injected bundle, GStreamer scanner/plugins, GIO modules and font
data. It preserves desktop/session and explicit GPU driver configuration. A
system bubblewrap is preferred when present, since distribution policy may be
attached to that path; otherwise the packaged bubblewrap is used.

This is still the existing CPU-frame proof of concept, not a new GPU import path.
The package retains graphics/media build support, but no hardware decode, audible
audio, GPU interoperability or performance improvement is claimed from packaging.
A Mesa EGL/Gallium fallback is bundled because this WPE build initializes EGL
even for CPU frames. GLVND vendor data, GLES and GSettings schemas are runtime
dependencies that are not discoverable from ELF `ldd` alone. Proprietary GPU
drivers remain host-provided; the launcher does not force software rendering.
Mesa/NVIDIA driver discovery and loader ABI
compatibility require real hardware tests and can require a revised loader policy.

## Installer and trust boundary

The setup window offers Install, Update or Repair for environments represented
by a verified release in the embedded `LinuxRuntimeCatalog.json`. It displays
version, download size, progress and errors. The normal screen has no source clone,
compiler, Makefile or system-package command. Developer details remain under
Advanced. No install/update runs without the user pressing its button.

Each catalog entry pins an exact runtime version, manifest SHA-256, ZIP SHA-256,
download byte count and explicitly tested distribution IDs/versions. The catalog
ships inside the plugin, so an untrusted remote manifest cannot authorize itself
by supplying its own checksum. Changing the remote release in place does not
change the pinned hashes. The installer uses HTTPS only, bounded GitHub asset
redirects, bounded download/extraction sizes and a 30-minute cancellation deadline.

The manifest hashes every regular file and records executable modes. Extraction
rejects symlinks, traversal, Windows device names, case collisions, duplicate or
unexpected entries, wrong sizes, missing files and bad hashes. Installation paths
with symlink/reparse ancestors are rejected. Files are extracted into a fresh
version/UUID directory. A fixed native permission command receives paths as
separate quoted arguments, with no path interpolation into shell code or root
privilege. A private completion file acknowledges the operation because Wine can
return from `start.exe` before the Unix child finishes.

Before activation, the installer runs the authenticated readiness probe against
the candidate's explicit path. It loads a tiny in-memory page and requires both its JavaScript
reply and an actual frame, catching child-process/sandbox failures beyond a
successful library version query. A failed probe removes the candidate and leaves
the previous receipt intact. After success, activation writes a small receipt
atomically. Existing runtime trees are retained so running screens keep
their files. Repair uses a fresh directory rather than overwriting mapped libraries.
Automatic pruning of older successful runtime versions is not implemented yet.
The installer never edits system repositories, packages, Wine prefixes, security
policy or GPU drivers. An invalid/missing managed runtime reports an error; it
does not silently install WebView2.

Release/update process:

1. Run the manual **Experimental Linux browser runtime** GitHub Actions workflow
   with a new numeric runtime version. It builds WPE/helper, packages the runtime,
   collects exact corresponding sources and runs standalone/clean-distro tests.
2. Keep candidate publication off until source provenance, standalone media and
   Wine test results have been reviewed. Optional publication creates a versioned
   prerelease only after workflow checks; an existing version is never replaced.
3. Add reviewed manifest/ZIP hashes and verified platform selections to the plugin
   catalog. Ship the catalog in a CrystalCast update. This intentionally requires
   a plugin update to trust a new runtime; a remotely mutable "latest" manifest is
   not trusted. Do not list distributions solely because they share glibc versions.
4. Keep matching source assets available alongside binaries, and treat WebKit and
   GStreamer security updates as a maintainer obligation for bundled runtimes.

## Sandbox and launcher limits

Ubuntu 24.04 desktop AppArmor policy can restrict unprivileged user namespaces.
A portable user-installed sandbox binary does not automatically inherit a
distribution-managed policy profile. WSL/container success does not test that
policy. Prefer a working system sandbox helper when present; otherwise report a
clear unsupported startup failure. Do not disable WebKit's sandbox, weaken global
kernel policy or silently install a system profile.
[Ubuntu user-namespace restrictions](https://documentation.ubuntu.com/security/security-features/privilege-restriction/apparmor/).

Wine's `start.exe /wait /unix` remains the launch mechanism. The plugin finds the
managed runtime before a developer/system helper. A self-contained native bootstrap
handles inherited Proton library settings, but it does not escape Flatpak or
Steam pressure-vessel filesystem/network boundaries. Installing files in a host
directory is not enough if the game cannot execute or map them there. Those setups
need explicit host/portal integration if the existing path fails.
[Flatpak filesystem boundaries](https://docs.flatpak.org/en/latest/sandbox-permissions.html).

Valve documents different container runtimes for different Proton generations:
Proton 8–10 use Steam Linux Runtime 3, while Proton 11+ use Runtime 4. Therefore a
recent host distribution does not establish the libc visible to a native child of
the game. Automatic installation is currently blocked when Flatpak or
pressure-vessel or Snap is detected, even if the exposed host distro matches a catalog
entry. A host-launch/portal bridge needs its own execution, environment and IPC
tests before those environments can be enabled.
[Valve Steam Runtime documentation](https://github.com/ValveSoftware/steam-runtime).

## Licenses and corresponding source

WPE contains LGPL/BSD and additional third-party components. GStreamer, FFmpeg
codecs, fonts, sandbox tools and their dependencies have their own licenses.
Open-source redistribution is feasible subject to each component's obligations;
the project's open-source status is not a substitute for satisfying them.
[WPE license overview](https://wpewebkit.org/about/faq.html),
[GNU corresponding-source guidance](https://www.gnu.org/licenses/gpl-faq.en.html).

The packager records the exact binary/source package versions owning bundled
distribution files, preserves their copyright notices/common license texts, and
copies WPE's third-party license files. `collect_sources.py` downloads the exact
source packages (including distro patches), the pinned WPE tarball and CrystalCast
source/build recipes. Publication requires those source archives alongside the
binary, not just upstream links or a checksum list. CI fails if an owner, license
notice or corresponding source cannot be collected. Dynamic libraries remain
separate for relinking/replacement. The static bootstrap also includes glibc and
GCC support code: their exact source packages and notices are collected, and its
relinkable application object plus source/build instructions accompany the source
bundle. The dynamic browser still uses the host libc/loader. Codec patent/distribution restrictions are
separate from source-code license compliance and need maintainer assessment for
the intended release jurisdictions.

## Verification status

At the start of this packaging work, the system-linked Arch helper had verified
frames, JavaScript/control IPC and local H.264/AAC/MSE playback. Those findings
remain in [the earlier test report](NATIVE_LINUX_BROWSER.md). They do not establish
that this new portable runtime works.

WPE 2.54.1 builds successfully on Ubuntu 24.04 with Clang 18. The version 0.1.0
candidate is approximately **231 MiB compressed / 626 MiB expanded**, containing
the helper, engine, media libraries, Mesa fallback, runtime data and licenses. It
is separate from the unchanged 13-file Windows plugin package. The source bundle
is a separate, larger download; ordinary users do not need it. The release folder
contains exact byte counts and SHA-256 values in `package-result.json` and the
per-file `runtime-manifest.json`.

| Environment tested | Local page / JS / frames | H.264/AAC fast-start MP4 and MSE | Desktop policy / FFXIV |
|---|---|---|---|
| Ubuntu 24.04 container | Pass | Pass | Unverified |
| Debian 13 container | Pass | Pass | Unverified |
| Fedora 44 container | Pass | Pass | Unverified |
| Arch container (2026-10-16 image) | Pass | Pass | Unverified |
| Ubuntu WSL + Wine 9 Windows C harness | Native launch, permission acknowledgement, loopback, shared mapping, JavaScript and cleanup pass | Not a game/player benchmark | Unverified |
| SteamOS / Steam Deck | Unverified | Unverified | Automatic installation disabled until launcher integration is tested |
| Flatpak / pressure-vessel / Snap | No verified host-launch bridge | Unverified | Automatic installation disabled |
| Ubuntu 22.04 / Debian 12 | Below this artifact's glibc baseline | Unsupported by this artifact | Requires a separately built/tested baseline |
| ARM64 | No artifact | Unverified | Not enabled |

Clean targets received only Python/test utilities and their CA certificate store,
not system WPE, GStreamer or Mesa. The package delivers real SHM compositor frames.
Missing-engine and failing-sandbox tests refuse startup before publishing IPC
files. A sandbox preflight runs before the helper, and the patched engine fails
closed if its container checks would otherwise disable sandboxing.

The reused YouTube page plays the user's `GExciu1OHuE` sample in the portable
runtime: 946 frames and 584 distinct sampled hashes in a 25-second check, with
advancing playback telemetry. Twitch live did not establish advancing playback
in the sample check; Dailymotion and Vimeo samples also failed to establish
playback. These results do not prove general provider incompatibility, but wider
provider coverage remains unverified. The direct fragmented-MP4 fixture stalled
when used as progressive media; fast-start MP4 and explicit MSE were verified.

A sequential clean-Ubuntu 1920x1080 H.264/AAC MSE test delivered about **60 fps
after the first second**, with 1.79 ms mean / 2.25 ms p95 export-to-consumer age,
about 524% helper-tree CPU (100% = one logical core), and 742 MiB summed RSS.
These are 12-second synthetic CPU-fallback measurements on a Ryzen 9 9955HX under
WSL, excluding the consumer and game. Summed RSS can double-count shared pages.
Four simultaneous distro test runs fell to roughly 46–49 fps per helper; they
establish functionality under contention, not single-screen performance. No
hardware decoding, GPU utilization, DXVK presentation latency, audible audio,
long-session stability or real-game smoothness is claimed.

The real C# package extractor accepted the candidate and verified every file.
The stronger C# readiness probe also verified the native engine's local-page
JavaScript and TCP frames from Windows through WSL. Debug and Release pass all
379 unit/rendering tests; Windows package validation passes. GitHub Actions has
not been run remotely yet. The standalone self-contained .NET 10 apphost crashes
during startup under Ubuntu Wine 9 and the official Wine-XIV 10.8 runner in this
environment, so the complete managed installer has not been exercised under
Wine/Dalamud. This does not establish failure inside Dalamud's different CLR host.

**Release gate:** no runtime is published or pinned in the plugin catalog yet.
Install is deliberately unavailable until a reviewed release is hosted and its
hashes/platform selections ship in the plugin. Full installer, desktop Wine and
FFXIV validation remain necessary before treating this as plug-and-play support.

Developer harnesses:

WPE remains the recommended long-term engine for its compositor frame export.
The CPU fallback establishes a working bridge, but its measured CPU cost makes
GPU-buffer interoperability the next performance investigation. Container-based
launchers also need a verified host-launch bridge before their Install flow can
be enabled. Neither issue is solved by changing the archive format.

The following commands are developer-only checks:

```sh
# Native ELF smoke test against a freshly unpacked package:
bash NativeLinuxBrowser/packaging/smoke_portable.sh runtime.zip NativeLinuxBrowser/tests

# MSE needs the fragmented fixture; progressive playback gets a fast-start MP4:
ffmpeg -i fragmented.mp4 -c copy -movflags +faststart progressive.mp4
bash NativeLinuxBrowser/packaging/smoke_portable.sh runtime.zip NativeLinuxBrowser/tests fragmented.mp4 /dev/null progressive.mp4

# Clean target only (no system WPE/bubblewrap): failure and cleanup checks:
python3 NativeLinuxBrowser/packaging/smoke_failures.py runtime.zip

# Windows PE harness, run as the desktop user in an isolated test Wine prefix:
x86_64-w64-mingw32-gcc -Os -s -Wall -Wextra -Werror \
  NativeLinuxBrowser/tests/wine_launch_probe.c -o wine-launch-probe.exe -lws2_32
WINEPREFIX=/path/to/test-prefix wine wine-launch-probe.exe /absolute/path/to/runtime/bin/crystalcast-browser

# Or unpack/test/clean automatically in a disposable test prefix, including a
# runtime directory containing spaces (requires developer Wine/MinGW tools):
bash NativeLinuxBrowser/packaging/smoke_wine.sh runtime.zip wine-launch-probe.exe
```

The clean-target script installs Python/test utilities and the distribution's CA
certificate store in Ubuntu/Debian/Fedora/Arch containers, never WPE or GStreamer.
Container seccomp adjustments only allow
the nested sandbox to run; WebKit's sandbox remains enabled. These tests cannot
validate distribution kernels, AppArmor/SELinux desktop policy, host audio or GPUs.
