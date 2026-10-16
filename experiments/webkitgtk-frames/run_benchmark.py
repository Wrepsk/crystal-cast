#!/usr/bin/env python3
"""Shareable desktop benchmark. Reports use allowlisted fields; nothing is uploaded."""
import argparse
import collections
import contextlib
import hashlib
import io
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import tempfile

import benchmark

ROOT = Path(__file__).resolve().parent
REPORT_VERSION = 1


def safe_text(value):
    """Only used for graphics/version/model descriptions, never arbitrary logs."""
    value = str(value)[:240]
    value = re.sub(r"(?:/|\\)[^\s,;]*", "[path removed]", value)
    value = re.sub(r"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[address removed]", value)
    value = re.sub(r"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b", "[identifier removed]", value)
    value = re.sub(r"\S+@\S+", "[account removed]", value)
    return "".join(c for c in value if c.isprintable())


def command(args):
    try:
        return subprocess.run(args, capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.TimeoutExpired):
        return None


def diagnostic_categories(log):
    """Convert private raw logs to fixed categories; never retain unknown log text."""
    lowered = log.lower()
    signatures = {
        "egl_initialization_problem": ("egl_not_initialized", "could not create egl", "could not create surfaceless egl", "failed to create dri2 screen"),
        "sandbox_or_permissions_problem": ("bwrap:", "creating new namespace failed", "permission denied"),
        "display_unavailable": ("cannot open display", "failed to open display"),
        "hardware_video_sink_unavailable": ("gl video sink dependencies", "hardware-accelerated zero-copy video rendering can't be enabled"),
        "media_decode_problem": ("missing decoder", "no suitable plugins", "media failed to load", "failed to decode"),
        "allocation_problem": ("out of memory", "failed to allocate"),
        "frame_consumer_or_protocol_problem": ("probe failed:",),
    }
    return [name for name, matches in signatures.items() if any(text in lowered for text in matches)]


def environment():
    release = {}
    try:
        for line in Path("/etc/os-release").read_text().splitlines():
            key, _, value = line.partition("=")
            value = value.strip('"\'')
            if key in ("ID", "VERSION_ID", "VARIANT_ID") and re.fullmatch(r"[a-zA-Z0-9_.-]{1,50}", value):
                release[key.lower()] = value
    except OSError:
        pass
    kernel = platform.release().lower()
    virtualization = "native-linux-no-vm-markers-detected"
    if "microsoft" in kernel or "wsl" in kernel:
        virtualization = "wsl2" if "wsl2" in kernel else "wsl"
    else:
        try:
            dmi = " ".join(Path("/sys/class/dmi/id", name).read_text().lower()
                           for name in ("sys_vendor", "product_name"))
            if any(marker in dmi for marker in ("qemu", "kvm", "vmware", "virtualbox", "virtual machine", "xen", "parallels", "bhyve")):
                virtualization = "virtual-machine"
        except OSError:
            pass
    gpu_devices = []
    for device in sorted(Path("/sys/class/drm").glob("card[0-9]*/device")):
        try:
            vendor, product = (device.joinpath(name).read_text().strip() for name in ("vendor", "device"))
            if not all(re.fullmatch(r"0x[0-9a-fA-F]{4}", v) for v in (vendor, product)):
                continue
            driver = device.joinpath("driver").resolve().name
            gpu_devices.append({"vendor_id": vendor, "device_id": product,
                                "kernel_driver": driver if re.fullmatch(r"[a-zA-Z0-9_-]{1,40}", driver) else "unknown"})
        except OSError:
            pass
    nvidia = []
    if shutil.which("nvidia-smi"):
        query = command(["nvidia-smi", "--query-gpu=name,driver_version", "--format=csv,noheader,nounits"])
        if query and query.returncode == 0:
            nvidia = [safe_text(line) for line in query.stdout.splitlines()[:8]]
    overrides = {}
    for key in ("GSK_RENDERER", "GALLIUM_DRIVER", "MESA_LOADER_DRIVER_OVERRIDE", "LIBGL_ALWAYS_SOFTWARE",
                "WEBKIT_SKIA_ENABLE_CPU_RENDERING", "WEBKIT_DMABUF_RENDERER_FORCE_SHM", "WEBKIT_GST_DISABLE_GL_SINK",
                "WEBKIT_DISABLE_COMPOSITING_MODE", "WEBKIT_DISABLE_DMABUF_RENDERER"):
        if key in os.environ:
            value = os.environ[key]
            overrides[key] = value if re.fullmatch(r"[a-zA-Z0-9_,.-]{1,40}", value) else "set (value omitted)"
    return {"distribution": release, "architecture": platform.machine(),
            "kernel_version": (re.match(r"[0-9.]+", kernel) or ["unknown"])[0],
            "session_hint": "wayland" if os.environ.get("WAYLAND_DISPLAY") else "x11" if os.environ.get("DISPLAY") else "none",
            "host_environment": virtualization, "virtualization_detection": "heuristic; absence of markers is not proof of bare metal",
            "drm_gpu_devices": gpu_devices, "nvidia_models_and_driver_versions": nvidia,
            "logical_cpu_count": os.cpu_count(), "rendering_overrides": overrides}


def dependencies(binary):
    if os.getuid() == 0:
        return None, ["Run this benchmark as your ordinary desktop user, without root or sudo."]
    if not binary.is_file():
        return None, ["The precompiled executable is missing. Extract the complete test package."]
    if not os.access(binary, os.X_OK):
        return None, ["Executable permission is missing. Run: chmod +x bin/gtk-frame-probe (or use the tar.gz package)."]
    check = command([str(binary), "--check"])
    if not check or check.returncode:
        missing = []
        output = (check.stderr + check.stdout) if check else ""
        missing += re.findall(r"(?:loading shared libraries: |^\s*)(lib[a-zA-Z0-9_.+-]+\.so(?:\.[0-9]+)*)(?::| => not found)", output, re.M)
        return None, ["Missing or incompatible system runtime libraries: " + (", ".join(sorted(set(missing))) or "GTK4/WebKitGTK 6.0/GStreamer or glibc ABI")]
    try:
        runtime = json.loads(check.stdout.strip())
    except (ValueError, TypeError):
        return None, ["The system runtime check did not produce valid diagnostics."]
    missing = [name for name in ("qtdemux", "h264parse", "videoconvert") if not runtime["factories"].get(name)]
    decoders = [v for k, v in runtime["factories"].items() if k.endswith("h264dec") or k == "avdec_h264"]
    if not any(decoders):
        missing.append("an H.264 decoder")
    problems = ["Missing GStreamer components: " + ", ".join(missing)] if missing else []
    if not (os.environ.get("WAYLAND_DISPLAY") or os.environ.get("DISPLAY")):
        problems.append("No graphical desktop session was found. Run in a terminal on your Linux desktop.")
    return runtime, problems


def acceleration(observations, counts, gsk_renderer):
    renderer = next((x.get("gl_renderer", "") for x in observations
                     if x["kind"] == "render_context"), "")
    if "Cairo" in gsk_renderer:
        return "mixed: CPU GSK renderer; browser GPU-backed texture observed; decoder unverified" if counts.get("browser_dmabuf", 0) or counts.get("browser_gl", 0) else "CPU GSK renderer and CPU-visible browser output observed"
    if renderer in ("", "not observable"):
        return "GPU renderer API selected; actual implementation not observable; hardware acceleration unverified"
    software = any(x in renderer.lower() for x in ("llvmpipe", "softpipe", "swrast", "software", "lavapipe"))
    if counts.get("browser_dmabuf", 0):
        return "browser-dmabuf-observed; " + ("software GTK context" if software else "hardware GTK context indicated; browser internal rendering/decode unverified")
    if software:
        return "software-rendering-observed"
    if counts.get("browser_cairo", 0) or counts.get("browser_memory", 0):
        return "mixed: CPU-visible browser output; GTK hardware context indicated; browser internal rendering/decode unknown"
    if counts.get("browser_gl", 0) and renderer:
        return "GPU browser texture and hardware GTK context indicated; hardware decode unverified"
    return "unknown; renderer/texture evidence insufficient"


def frame_sequence(frames):
    """Unwrap the counter, retaining resets/reordering rather than inventing skips."""
    positions = []
    position = backwards = 0
    ambiguous = False
    for index, frame in enumerate(frames):
        if index:
            previous = frames[index - 1]
            gap = (frame["time_us"] - previous["time_us"]) / 1e6
            if gap >= 512 / 60:
                ambiguous = True  # Too long to disambiguate forward vs backward modulo jumps.
            delta = (frame["marker"] - previous["marker"]) % 1024
            if delta > 512:
                delta -= 1024
                backwards += 1
            position += delta
        positions.append(position)
    distinct = len(set(positions))
    skipped = max(positions) - min(positions) + 1 - distinct if positions and not backwards and not ambiguous else None
    return positions, len(positions) - distinct, skipped, backwards, ambiguous


def analyze(summary, events, consumer, overlay=False):
    frames = [e for e in events if e["kind"] == "frame"]
    unavailable = next((e for e in events if e["kind"] == "unavailable"), None)
    observations = []
    for e in summary.get("observations", []):
        observations.append({k: safe_text(v) if isinstance(v, str) else v for k, v in e.items() if k != "reason"})
    if unavailable:
        return {"status": "unavailable", "reason": safe_text(unavailable["reason"]), "observations": observations}
    user_closed = any(e["kind"] == "user_closed_window" for e in events)
    if not frames:
        return {"status": "unavailable", "reason": "Window closed by user; benchmark cancelled" if user_closed else "Browser startup, local playback, or frame extraction did not complete; no performance claim", "observations": observations}
    audited = [f for f in frames if f["downloaded"]]
    duration = summary["measured_window_seconds"]
    fully_audited = len(consumer) == len(frames) and bool(consumer)
    consumer_by_seq = {f["seq"]: f for f in consumer}
    corrupt = sum(not f["bars_valid"] or (overlay and not f["overlay_valid"]) for f in audited)
    corrupt += sum(f["widget_width"] != 1920 or f["widget_height"] != 1080 for f in frames)
    mismatches = 0
    for f in frames:
        other = consumer_by_seq.get(f["seq"])
        if consumer and (not other or other["marker"] != f["marker"] or other["sample_hash"] != f["sample_hash"]):
            mismatches += 1
    markers = [f["marker"] for f in audited]
    deltas = [(b - a) % 1024 for a, b in zip(markers, markers[1:])]
    positions, repeated, skipped, backwards, ambiguous = frame_sequence(audited)
    if not fully_audited:
        skipped = None
    media = [e for e in events if e["kind"] == "media"]
    media_ok = bool(media) and not any(e["data"]["error"] for e in media)
    moving = len(set(markers)) > 1
    needs_consumer = summary["options"]["mode"] in ("shm", "render-shm")
    coverage_ok = fully_audited if needs_consumer else not consumer
    integrity_ok = summary["exit_code"] == 0 and not (corrupt or mismatches or summary["consumer_errors"]) and coverage_ok and media_ok and moving
    unique = len(set(positions)) if fully_audited and integrity_ok and not ambiguous else None
    baseline = summary["measurement_start_us"] + summary["options"]["warmup"] * 1e6
    buckets = collections.Counter()
    seen = set()
    for index, frame in enumerate(frames):
        if fully_audited and positions[index] not in seen:
            buckets[int((frame["time_us"] - baseline) // 1000000)] += 1
            seen.add(positions[index])
    per_second = [buckets[i] for i in range(math.floor(duration))]
    unique_fps = unique / duration if unique is not None else None
    complete = not user_closed and duration >= summary["options"].get("seconds", duration) - .02
    sustained = bool(complete and unique_fps is not None and duration >= 10 and unique_fps >= 59 and
                     per_second and min(per_second) >= 57 and not backwards and not ambiguous and
                     skipped is not None and skipped <= duration * 60 * .02)
    counts = {key: sum(f[key] > 0 for f in frames) for key in
              ("browser_dmabuf", "browser_gl", "browser_memory", "browser_cairo", "uninspected_nodes")}
    measured_media = [e["data"] for e in media if e["time_us"] >= baseline]
    drops = (max(0, measured_media[-1]["dropped"] - measured_media[0]["dropped"]) if len(measured_media) > 1 else None)
    anomalies = [{"audited_frame": index + 1, "previous_marker": audited[index - 1]["marker"],
                  "marker": frame["marker"], "gap_ms": round((frame["time_us"] - audited[index - 1]["time_us"]) / 1000, 3)}
                 for index, frame in enumerate(audited) if index and
                 ((frame["marker"] - audited[index - 1]["marker"]) % 1024 > 512 or
                  frame["time_us"] - audited[index - 1]["time_us"] >= 512 / 60 * 1e6)][:32]
    return {"status": ("ok" if complete else "incomplete") if integrity_ok else "invalid", "integrity_passed": integrity_ok,
            "measurement_complete": complete,
            "native_exit_code": summary["exit_code"],
            "observations": observations, "renderer": summary["environment"][0]["renderer"],
            "display_backend": summary["environment"][0]["display"],
            "versions": {k: summary["environment"][0][k] for k in ("gtk", "webkit")},
            "acceleration_classification": acceleration(observations, counts, summary["environment"][0]["renderer"]),
            "browser_node_frames": counts, "output_texture_counts": summary["output_textures"],
            "geometry": next((e for e in observations if e["kind"] == "geometry"), None),
            "direct_frames": summary["direct_frames"], "frames_observed": len(frames),
            "audited_frames": len(audited), "fully_independently_verified": fully_audited and integrity_ok,
            "invalid_frames_or_dimensions": corrupt, "consumer_mismatches": mismatches,
            "duration_seconds": duration, "texture_submission_fps": len(frames) / duration,
            "verified_unique_fps": unique_fps, "verified_unique_frames": unique,
            "repeated_frames": repeated if fully_audited else None,
            "out_of_order_or_counter_reset_events": backwards if fully_audited else None,
            "counter_unwrap_ambiguous": ambiguous,
            "counter_anomalies": anomalies,
            "skipped_source_frames": skipped, "audited_marker_changes": sum(d > 0 for d in deltas),
            "browser_reported_dropped_frames_delta": drops,
            "backpressure_capture_drops": summary["end"][0]["drops"],
            "verified_unique_frames_per_second": per_second if fully_audited else None,
            "sustained_1080p60_verified": sustained,
            "sustained_criteria": ">=59 verified unique fps, >=57 in each complete 1s bucket, <=2% skipped input frames, >=10s, requested window completed, integrity passed, no resets/reordering/ambiguity",
            "extraction_us": summary["extraction_us"], "unaudited_submission_us": summary["unaudited_total_us"],
            "readback_or_download_us": benchmark.distribution([f["download_us"] for f in audited]),
            "consumer_copy_and_validation_us": summary["consumer_copy_us"],
            "extraction_start_to_consumer_us": summary["consumer_age_us"],
            "helper_tree_cpu_percent": summary["tree_cpu_percent"], "helper_tree_peak_summed_rss_mib": summary["tree_rss_peak_mib"],
            "benchmark_tree_cpu_percent": summary.get("benchmark_tree_cpu_percent"),
            "benchmark_tree_peak_summed_rss_mib": summary.get("benchmark_tree_peak_summed_rss_mib"),
            "copies": "download directly into mapped BGRA slot; full independent consumer copy; game upload excluded" if consumer else "GSK rendering or direct texture reference; sparse full readbacks for audit; no external GPU delivery measured",
            "throughput_note": "Fully audited CPU delivery" if fully_audited else "Submission cadence only; use render-shm/shm companion for independently verified delivery; unaudited frames cannot prove 60fps"}


def summarize(report):
    lines = ["CrystalCast WebKitGTK desktop frame benchmark", "",
             "Local hardware validation only. No Wine/DXVK/FFXIV compatibility claim.",
             "No results were uploaded. Send summary.txt and results.json from this folder.", "",
             "Distribution: " + json.dumps(report["environment"]["distribution"]),
             "Environment: " + report["environment"]["host_environment"],
             "Session hint: " + report["environment"]["session_hint"], ""]
    lines += report.get("dependency_problems", [])
    if report.get("cancelled"):
        lines += ["Cancelled by the user. Only completed tests below are available."]
    if report.get("runner_error"):
        lines += [report["runner_error"]]
    for name, result in report["tests"].items():
        lines += [name + ": " + result["status"]]
        if result["status"] == "unavailable":
            lines += ["  " + result["reason"]]
            continue
        fps = result["verified_unique_fps"]
        lines += [f"  Renderer: {result['renderer']}; output textures: {result['output_texture_counts']}",
                  "  Acceleration: " + result["acceleration_classification"],
                  "  Browser DMA-BUF texture frames observed: " + str(result["browser_node_frames"]["browser_dmabuf"]),
                  "  Frames with uninspected browser nodes: " + str(result["browser_node_frames"]["uninspected_nodes"]),
                  f"  Verified unique fps: {fps:.2f}" if fps is not None else f"  Submission fps: {result['texture_submission_fps']:.2f} (not verified delivery)",
                  f"  Repeated / skipped source / backpressure drops: {result['repeated_frames'] if result['repeated_frames'] is not None else 'not measured'} / {result['skipped_source_frames'] if result['skipped_source_frames'] is not None else 'not established'} / {result['backpressure_capture_drops']}",
                  f"  Counter resets/reordering: {result['out_of_order_or_counter_reset_events']}; ambiguous unwrap: {result['counter_unwrap_ambiguous']}",
                  f"  Extraction mean / p95: {result['extraction_us']['total_us']['mean']/1000:.3f} / {result['extraction_us']['total_us']['p95']/1000:.3f} ms",
                  f"  Download/readback mean / p95: {result['readback_or_download_us']['mean']/1000:.3f} / {result['readback_or_download_us']['p95']/1000:.3f} ms" if result["readback_or_download_us"] else "  Download/readback: not measured",
                  f"  Helper CPU: {result['helper_tree_cpu_percent']}% (100% = one logical CPU); peak summed RSS: {result['helper_tree_peak_summed_rss_mib']} MiB",
                  f"  Including runner/consumer: CPU {result['benchmark_tree_cpu_percent']}%; peak summed RSS {result['benchmark_tree_peak_summed_rss_mib']} MiB",
                  "  Integrity passed: " + str(result["integrity_passed"]),
                  "  Sustained verified 1080p60: " + ("YES" if result["sustained_1080p60_verified"] else "NOT ESTABLISHED"), ""]
    lines += ["Texture submission timing excludes GPU completion and external delivery.",
              "CPU/RSS are sampled helper-tree estimates, excluding this runner and the desktop compositor; RSS can double-count sharing.",
              "Hardware decode is unverified. DMA-BUF import capability/format counts do not establish export to Wine/DXVK.",
              "Full browser allocation is 1920x1080; the mapped desktop viewport is smaller. Do not minimize/hide the window.",
              "60fps criteria: >=59 independently verified unique fps; >=57 each full second; <=2% skipped source frames; >=10s; all integrity checks pass."]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path.cwd() / "webkitgtk-results")
    parser.add_argument("--seconds", type=int, default=12, choices=range(2, 16), metavar="2..15")
    parser.add_argument("--warmup", type=int, default=3, choices=range(1, 5), metavar="1..4")
    parser.add_argument("--check", action="store_true", help="Only check runtime dependencies and write an availability report")
    parser.add_argument("--diagnostics", action="store_true", help="Write additional allowlisted fallback/error categories; raw logs are not shared")
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        parser.error("Choose an empty results directory with --output; existing results are preserved.")
    args.output.mkdir(parents=True, exist_ok=True)
    binary = ROOT / "bin/gtk-frame-probe"
    runtime, problems = dependencies(binary)
    report = {"schema_version": REPORT_VERSION, "environment": environment(), "runtime": runtime,
              "dependency_problems": problems, "tests": {}, "privacy": "Allowlisted hardware/runtime/performance fields only. No uploads; no usernames, home paths, addresses, hostnames or machine IDs.",
              "integration_scope": "Native standalone widget/frame benchmark only; Wine, DXVK and FFXIV remain untested."}
    video = ROOT / "data/video.mp4"
    try:
        manifest = json.loads((ROOT / "manifest.json").read_text())
        if any(hashlib.sha256((ROOT / name).read_bytes()).hexdigest() != expected
               for name, expected in manifest["sha256"].items()):
            problems.append("Package integrity check failed. Download and extract a fresh complete package.")
        report["package"] = {k: manifest[k] for k in ("version", "build_commit", "target", "fixture")}
        report["package"]["fixture_sha256"] = manifest["sha256"]["data/video.mp4"]
        report["package"]["executable_sha256"] = manifest["sha256"]["bin/gtk-frame-probe"]
    except (OSError, ValueError, KeyError):
        problems.append("Package manifest or bundled video is missing; use the complete downloadable package.")
    if not args.check and not problems:
        print("The benchmark will open one small window at a time for about 70 seconds.")
        print("Keep it mapped and visible; do not minimize it. Ctrl+C cancels. No uploads or package changes.")
        try:
            with tempfile.TemporaryDirectory(prefix="crystalcast-desktop-bench-") as temporary:
                for mode in ("texture", "direct", "shm", "render-shm", "overlay-check"):
                    print("Running " + mode + "...", flush=True)
                    destination = Path(temporary) / mode
                    options = ["--mode", "render-shm" if mode == "overlay-check" else mode,
                               "--seconds", "4" if mode == "overlay-check" else str(args.seconds),
                               "--warmup", str(args.warmup), "--audit-every", "30", "--wait-for-video",
                               "--window-width", "960", "--window-height", "540", "--fixture", str(video),
                               "--binary", str(binary), "--output", str(destination)]
                    if mode == "direct":
                        options += ["--require-direct"]
                    if mode == "overlay-check":
                        options += ["--overlay"]
                    try:
                        with contextlib.redirect_stdout(io.StringIO()):
                            benchmark.main(options)
                    except SystemExit:
                        pass  # Native unavailability and correctness failures are classified from evidence.
                    if not (destination / "summary.json").exists():
                        report["tests"][mode] = {"status": "unavailable", "reason": "Native process or runtime startup did not produce a completed result"}
                        continue
                    summary = json.loads((destination / "summary.json").read_text())
                    events = [json.loads(line) for line in (destination / "events.jsonl").read_text().splitlines()]
                    consumer = [json.loads(line) for line in (destination / "consumer.jsonl").read_text().splitlines()]
                    report["tests"][mode] = analyze(summary, events, consumer, mode == "overlay-check")
                    report["tests"][mode]["diagnostic_categories"] = diagnostic_categories((destination / "stderr.log").read_text(errors="replace"))
                    if any(e["kind"] == "user_closed_window" for e in events):
                        report["cancelled"] = True
                        break
        except KeyboardInterrupt:
            report["cancelled"] = True
        except Exception:
            # Exception text can contain paths/addresses; never copy it into a shareable report.
            report["runner_error"] = "Benchmark could not finish; check extraction permissions, available memory and system runtime. Partial results are retained."
    (args.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
    (args.output / "summary.txt").write_text(summarize(report))
    if args.diagnostics:
        diagnostic = {"dependency_problems": problems,
                      "modes": {name: {k: value for k, value in result.items() if k in
                                ("status", "reason", "observations", "acceleration_classification", "copies", "throughput_note", "diagnostic_categories")}
                                for name, result in report["tests"].items()}}
        (args.output / "diagnostic.log").write_text(json.dumps(diagnostic, indent=2) + "\n")
    print("Finished. Send these local files: summary.txt and results.json in your results folder.")
    if args.diagnostics:
        print("You may also send diagnostic.log; raw browser logs were discarded.")
    print("Results folder: " + str(args.output.resolve()))
    if problems:
        print("Runtime dependencies are unavailable; see summary.txt and READ-ME.txt. Nothing was installed.")
    if report.get("cancelled"):
        return 130
    if problems or report.get("runner_error"):
        return 2
    return 1 if any(x["status"] == "invalid" for x in report["tests"].values()) else 0


if __name__ == "__main__":
    raise SystemExit(main())
