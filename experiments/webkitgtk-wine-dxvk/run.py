#!/usr/bin/env python3
"""Developer-only native GTK -> leased BGRA -> Wine/D3D11/DXVK experiment."""
import argparse
import fcntl
import functools
import hashlib
import hmac
import http.server
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parent
NATIVE = ROOT / "native" if (ROOT / "native").is_dir() else ROOT.parent / "webkitgtk-frames"
sys.path.insert(0, str(NATIVE))
import benchmark  # noqa: E402 -- reuse fixture, HTTP serving, statistics and CPU sampler
import run_benchmark as native_report  # noqa: E402

FRAME_BYTES = benchmark.FRAME_BYTES


def receive_line(stream):
    line = stream.readline(4097)
    if not line.endswith(b"\n") or len(line) > 4096:
        raise ValueError("Incomplete or oversized consumer message")
    value = json.loads(line)
    if not isinstance(value, dict):
        raise ValueError("Invalid consumer message")
    return value


def analyze(events, delivered, consumer_start, errors, exits, seconds, warmup, mode, dxvk_version):
    frames = [e for e in events if e.get("kind") == "frame"]
    by_seq = {e["seq"]: e for e in frames}
    start = next((e["time_us"] for e in events if e.get("kind") == "measurement_start"), None)
    end = next((e["time_us"] for e in events if e.get("kind") == "end"), None)
    baseline = start + warmup * 1e6 if start is not None else None
    duration = (end - baseline) / 1e6 if end is not None and baseline is not None else 0
    # Include the final queued delivery and Present response, not just native capture time.
    complete_duration = max(duration, (max(x["reply_time_us"] for x in delivered) - baseline) / 1e6) if delivered and baseline is not None else duration
    mismatch = sum(not x.get("source_valid") or x.get("marker") != by_seq.get(x.get("seq"), {}).get("marker") or
                   x.get("sample_hash") != by_seq.get(x.get("seq"), {}).get("sample_hash") for x in delivered)
    gpu_audits = [x for x in delivered if x.get("gpu_audited")]
    gpu_bad = sum(not x.get("gpu_valid") or x.get("gpu_marker") != x.get("marker") for x in gpu_audits)
    native_bad = sum(not e.get("bars_valid") or e.get("widget_width") != 1920 or e.get("widget_height") != 1080 for e in frames)
    all_delivered = bool(frames) and len(delivered) == len(frames) and {x["seq"] for x in delivered} == set(by_seq)
    complete = duration >= seconds - .05
    media = [e["data"] for e in events if e.get("kind") == "media"]
    media_ok = bool(media) and not any(x.get("error") for x in media) and any(x.get("width") == 1920 and x.get("height") == 1080 for x in media)
    integrity = (all_delivered and not (mismatch or gpu_bad or native_bad or errors) and exits == [0, 0] and media_ok and
                 bool(dxvk_version) and bool(gpu_audits) and all(x.get("presented") for x in delivered))
    verified = integrity and len(gpu_audits) == len(delivered)
    sequence = [by_seq[x["seq"]] for x in delivered if x["seq"] in by_seq]
    positions, repeats, skipped, backwards, ambiguous = native_report.frame_sequence(sequence)
    unique = len(set(positions))
    buckets, delivery_buckets = [], []
    if baseline is not None:
        for bucket in range(int(duration)):
            buckets.append(len({position for position, frame in zip(positions, sequence)
                                if bucket <= (frame["time_us"] - baseline) / 1e6 < bucket + 1}))
            delivery_buckets.append(len({position for position, response in zip(positions, delivered)
                                         if bucket <= (response["reply_time_us"] - baseline) / 1e6 < bucket + 1}))
    fps = unique / complete_duration if complete_duration > 0 and integrity and not ambiguous else None
    sustained = bool(verified and complete and complete_duration >= 10 and fps is not None and fps >= 59 and
                     buckets and min(buckets) >= 57 and delivery_buckets and min(delivery_buckets) >= 57 and not backwards and not ambiguous and
                     skipped is not None and skipped / max(1, unique + skipped) <= .02)
    end_event = next((x for x in events if x.get("kind") == "end"), {})
    native_start = next((x for x in events if x.get("kind") == "start"), {})
    adapter = consumer_start.get("adapter", "").lower()
    acceleration = "software Vulkan device observed" if any(x in adapter for x in ("llvmpipe", "lavapipe", "software", "softpipe")) else "hardware adapter indicated; browser decode and rendering not independently established" if adapter else "unavailable"
    return {"status": "ok" if integrity and complete else "invalid" if frames else "unavailable",
            "mode": mode, "dxvk_version": dxvk_version, "consumer": consumer_start,
            "consumer_acceleration": acceleration,
            "native_runtime": {key: native_start.get(key) for key in ("gtk", "webkit", "renderer", "display")},
            "errors": errors, "native_and_wine_exit_codes": exits, "measurement_complete": complete,
            "native_capture_window_seconds": duration, "complete_delivery_window_seconds": complete_duration,
            "native_frames": len(frames), "delivered_frames": len(delivered), "gpu_audited_frames": len(gpu_audits),
            "native_invalid_frames": native_bad, "source_mismatches": mismatch, "gpu_mismatches": gpu_bad,
            "integrity_passed": integrity, "every_frame_verified_after_d3d11_upload": verified,
            "source_verified_unique_fps": fps, "complete_path_verified_unique_fps": fps if verified else None,
            "unique_frames": unique, "repeated_frames": repeats, "skipped_source_frames": skipped,
            "counter_resets_or_reordering": backwards, "counter_ambiguous": ambiguous,
            "unique_source_frames_per_second": buckets, "backpressure_capture_drops": end_event.get("drops"),
            "unique_delivered_frames_per_second": delivery_buckets,
            "sustained_verified_1080p60": sustained,
            "native_extraction_us": benchmark.distribution([x["total_us"] for x in frames]),
            "native_download_us": benchmark.distribution([x["download_us"] for x in frames]),
            "consumer_timings_us": {key: benchmark.distribution([x[key] for x in delivered
                 if key != "gpu_readback_validation_us" or x["gpu_audited"]]) for key in
                 ("source_check_us", "update_api_us", "gpu_copy_completion_us", "gpu_readback_validation_us", "present_api_us", "consumer_total_us")},
            "capture_start_to_present_ack_us": benchmark.distribution([x["reply_time_us"] - by_seq[x["seq"]]["time_us"]
                 for x in delivered if x["seq"] in by_seq]),
            "observations": [{k: native_report.safe_text(v) if isinstance(v, str) else v for k, v in x.items()}
                 for x in events if x.get("kind") in ("graphics", "render_context", "geometry", "dmabuf_capabilities")],
            "browser_dmabuf_frames": sum(x.get("browser_dmabuf", 0) > 0 for x in frames),
            "output_texture_types": sorted({x["texture"] for x in frames})}


def stop(process):
    if process and process.poll() is None:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            return
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()


def run_pass(args, mode, wine_env, executable):
    events, delivered, errors, samples, ticks = [], [], [], [], {}
    consumer_start = {}
    native = wine = None
    with tempfile.TemporaryDirectory(prefix="crystalcast-wine-frames-") as folder:
        private = Path(folder)
        os.chmod(private, 0o700)
        (private / "index.html").write_text(benchmark.page(False, args.overlay))
        ready, finished = threading.Event(), threading.Event()

        class Page(benchmark.Handler):
            def do_GET(self):
                if self.path in ("/", "/index.html") and not ready.wait(25):
                    self.send_error(503)
                    return
                super().do_GET()

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0),
            functools.partial(Page, directory=str(private), video_path=args.fixture))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(4)
        listener.settimeout(.2)
        token = secrets.token_hex(32)
        parent, child = socket.socketpair(socket.AF_UNIX, socket.SOCK_SEQPACKET)
        parent.settimeout(.2)
        mapping = private / "frames.bgra"
        environment = wine_env | {"CRYSTALCAST_BENCH_TOKEN": token, "DXVK_LOG_PATH": str(private), "DXVK_LOG_LEVEL": "info"}

        def relay():
            nonlocal consumer_start
            connection = None
            deadline = time.monotonic() + 25
            try:
                # Rejected unauthenticated peers cannot acquire or release a buffer lease.
                while not finished.is_set() and time.monotonic() < deadline:
                    try:
                        candidate, _ = listener.accept()
                    except socket.timeout:
                        continue
                    candidate.settimeout(1)
                    try:
                        auth = bytearray()
                        while len(auth) < 64:
                            chunk = candidate.recv(64 - len(auth))
                            if not chunk:
                                break
                            auth.extend(chunk)
                        if hmac.compare_digest(auth, token.encode()):
                            connection = candidate
                            break
                    except (OSError, TimeoutError):
                        pass
                    candidate.close()
                if connection is None:
                    raise TimeoutError("Consumer authentication did not complete")
                connection.settimeout(15)
                with connection, connection.makefile("rb") as stream:
                    hello = receive_line(stream)
                    if hello.get("kind") != "consumer_start" or (hello.get("width"), hello.get("height")) != (1920, 1080):
                        raise ValueError("Invalid consumer initialization")
                    consumer_start = {k: native_report.safe_text(v) if isinstance(v, str) else v for k, v in hello.items()}
                    ready.set()
                    while not finished.is_set():
                        try:
                            notice = parent.recv(24)
                        except socket.timeout:
                            continue
                        except ConnectionResetError:
                            break
                        if not notice:
                            break
                        if len(notice) != 24:
                            raise ValueError("Malformed native notice")
                        seq, slot, size, timestamp = struct.unpack("<QIIq", notice)
                        if not seq or slot >= 3 or size != FRAME_BYTES:
                            raise ValueError("Invalid leased slot")
                        connection.sendall(notice)
                        response = receive_line(stream)
                        if response.get("kind") != "consumer_frame" or response.get("seq") != seq:
                            raise ValueError("Mismatched consumer acknowledgement")
                        response["reply_time_us"] = time.monotonic_ns() / 1000
                        delivered.append(response)
                        # Release only after upload, optional GPU verification, and Present.
                        try:
                            parent.sendall(struct.pack("<Q", seq))
                        except (BrokenPipeError, ConnectionResetError):
                            pass  # Last published frames remain readable after producer exit.
                    connection.sendall(bytes(24))
            except Exception:
                errors.append("Frame relay, authentication, or consumer protocol did not complete")
                ready.set()
                stop(native)

        def read_native():
            for line in native.stdout:
                try:
                    item = json.loads(line)
                    if isinstance(item, dict) and "kind" in item:
                        events.append(item)
                except ValueError:
                    pass

        def sample():
            while not finished.wait(.1):
                tree = benchmark.process_tree(os.getpid())
                ticks.update({pid: x[1] for pid, x in tree.items()})
                samples.append((time.monotonic_ns() / 1000, sum(ticks.values()), sum(x[2] for x in tree.values())))

        with (private / "native.log").open("w") as native_log, (private / "wine.log").open("w") as wine_log:
            try:
                command = [str(args.native_binary), "--uri", f"http://127.0.0.1:{server.server_port}/", "--mode", "render-shm",
                    "--seconds", str(args.seconds), "--warmup", str(args.warmup), "--wait-for-video", "--window-width", "960",
                    "--window-height", "540", "--shm", str(mapping), "--ack-fd", str(child.fileno())]
                native = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=native_log, text=True,
                    pass_fds=(child.fileno(),), start_new_session=True)
                child.close()
                reader = threading.Thread(target=read_native)
                reader.start()
                deadline = time.monotonic() + 10
                while (not mapping.exists() or mapping.stat().st_size != 3 * FRAME_BYTES) and time.monotonic() < deadline and native.poll() is None:
                    time.sleep(.01)
                if not mapping.exists():
                    raise RuntimeError("Native mapping unavailable")
                # The harness deliberately requires Wine's ordinary Z: Unix filesystem mapping.
                windows_mapping = "Z:" + str(mapping).replace("/", "\\")
                wine = subprocess.Popen([args.wine, str(executable), windows_mapping, str(listener.getsockname()[1]),
                    mode, "overlay" if args.overlay else "plain"], env=environment,
                    stdout=wine_log, stderr=wine_log, start_new_session=True)
                bridge = threading.Thread(target=relay)
                bridge.start()
                sampler = threading.Thread(target=sample)
                sampler.start()
                native.wait(timeout=args.seconds + args.warmup + 40)
                reader.join(timeout=5)
                bridge.join(timeout=20)
                wine.wait(timeout=20)
            except Exception:
                errors.append("Native browser or Wine/DXVK process failed to start or finish")
            finally:
                stop(native)
                stop(wine)
                finished.set()
                ready.set()
                parent.close()
                child.close()
                listener.close()
                for thread in (locals().get("bridge"), locals().get("sampler"), locals().get("reader")):
                    if thread:
                        thread.join(timeout=3)
                server.shutdown()
                server.server_close()
        dxvk_logs = "\n".join(x.read_text(errors="replace") for x in private.glob("*_d3d11.log"))
        wine_text = (private / "wine.log").read_text(errors="replace")
        # DXVK can send its header to stderr while the per-API file starts later.
        dxvk_logs += "\n" + wine_text
        version = re.search(r"DXVK:\s+(v?[0-9][a-zA-Z0-9._+-]*)", dxvk_logs)
        report = analyze(events, delivered, consumer_start, errors,
            [native.returncode if native else None, wine.returncode if wine else None], args.seconds, args.warmup, mode,
            version[1] if version else None)
        baseline = next((x["time_us"] + args.warmup * 1e6 for x in events if x.get("kind") == "measurement_start"), float("inf"))
        measured = [x for x in samples if x[0] >= baseline]
        report["benchmark_tree_cpu_percent"] = round((measured[-1][1] - measured[0][1]) / os.sysconf("SC_CLK_TCK") /
            ((measured[-1][0] - measured[0][0]) / 1e6) * 100, 2) if len(measured) > 1 else None
        report["benchmark_tree_peak_summed_rss_mib"] = round(max(x[2] for x in measured) / 1024**2, 2) if measured else None
        report["diagnostic_categories"] = native_report.diagnostic_categories((private / "native.log").read_text(errors="replace") +
            (private / "wine.log").read_text(errors="replace") + dxvk_logs)
        report["dxvk_initialization_problem"] = any(x in dxvk_logs.lower() for x in ("failed to create", "no adapters found", "required extension"))
        failure = re.search(r"Consumer exception (0x[0-9a-fA-F]+) during ([a-zA-Z ]+)\n", wine_text)
        report["consumer_exception"] = {"code": failure[1], "phase": failure[2]} if failure else None
        module = re.search(r"Exception module: ([a-zA-Z0-9_.-]+) \+ (0x[0-9a-f]+)", wine_text)
        report["exception_module"] = {"name": module[1], "offset": module[2]} if module else None
        failure = re.search(r"([A-Za-z0-9/ ]+) failed: HRESULT (0x[0-9a-fA-F]+)", wine_text)
        report["d3d11_error"] = {"operation": failure[1].strip(), "hresult": failure[2]} if failure else None
        return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wine", default=shutil.which("wine") or "/usr/lib/wine/wine64")
    parser.add_argument("--dxvk-dir", type=Path, required=True, help="Directory containing existing x64 d3d11.dll and dxgi.dll")
    parser.add_argument("--wine-prefix", type=Path,
                        default=Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache")) / "crystalcast-frame-harness/wine-prefix",
                        help="Dedicated harness prefix on a Linux filesystem; existing game prefixes are refused")
    parser.add_argument("--fixture", type=Path, required=True, help="Existing encoded-counter video from the native benchmark")
    parser.add_argument("--native-binary", type=Path, default=NATIVE / "build/gtk-frame-probe")
    parser.add_argument("--consumer", type=Path, default=ROOT / "build/wine-frame-consumer.exe")
    parser.add_argument("--output", type=Path, default=Path.cwd() / "wine-dxvk-results")
    parser.add_argument("--mode", choices=("both", "upload", "verify"), default="both")
    parser.add_argument("--seconds", type=int, choices=range(2, 16), default=12)
    parser.add_argument("--warmup", type=int, choices=range(1, 5), default=3)
    parser.add_argument("--overlay", action="store_true")
    args = parser.parse_args()
    if sys.platform != "linux" or os.getuid() == 0:
        parser.error("Run as an ordinary Linux desktop user")
    for key in ("fixture", "native_binary", "consumer", "dxvk_dir", "wine_prefix", "output"):
        setattr(args, key, getattr(args, key).resolve())
    for path in (args.fixture, args.native_binary, args.consumer, args.dxvk_dir / "d3d11.dll", args.dxvk_dir / "dxgi.dll"):
        if not path.is_file():
            parser.error("A required video, executable, or DXVK DLL is missing")
    marker = args.wine_prefix / ".crystalcast-frame-harness"
    if args.wine_prefix.exists() and any(args.wine_prefix.iterdir()) and not marker.exists():
        parser.error("Use a new dedicated prefix; this harness refuses an existing unmarked Wine prefix")
    if args.output.exists() and any(args.output.iterdir()):
        parser.error("Choose an empty output directory")
    args.output.mkdir(parents=True, exist_ok=True)
    args.wine_prefix.mkdir(parents=True, exist_ok=True)
    os.chmod(args.wine_prefix, 0o700)
    marker.touch(mode=0o600)
    prefix_lock = marker.open("r+")
    try:
        fcntl.flock(prefix_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        parser.error("Another harness is using this dedicated prefix; wait for it to finish")
    wine_env = os.environ | {"WINEPREFIX": str(args.wine_prefix), "WINEARCH": "win64", "WINEDEBUG": "-all",
                             "WINEDLLOVERRIDES": "d3d11,dxgi=n"}
    report = {"environment": native_report.environment(), "wine_version": "unknown", "passes": {},
        "scope": "Standalone native GTK -> BGRA file mapping -> Wine/D3D11/DXVK. No game/CLR/plugin, DMA-BUF export, desktop scanout, or zero-copy claim.",
        "timing_notes": "UpdateSubresource API time is not completed upload time. GPU copy completion waits an event query after upload and backbuffer copy. Verification adds staging readback and full byte comparison. Present(0) return is not scanout. End-to-end uses Linux capture and reply clocks; no cross-clock subtraction.",
        "resource_notes": "100% CPU is one logical core. Tree sampling can miss detached Wine servers and excludes compositor; summed RSS double-counts sharing.",
        "files": {"fixture_sha256": hashlib.sha256(args.fixture.read_bytes()).hexdigest(),
                  "consumer_sha256": hashlib.sha256(args.consumer.read_bytes()).hexdigest()}, "errors": []}
    try:
        version = subprocess.run([args.wine, "--version"], capture_output=True, text=True, timeout=10)
        if version.returncode:
            raise RuntimeError("Wine unavailable")
        report["wine_version"] = native_report.safe_text(version.stdout.strip())
        print("Initializing the dedicated test prefix; existing game prefixes are not used.", flush=True)
        if not (args.wine_prefix / "system.reg").is_file():
            subprocess.run([args.wine, "wineboot", "-u"], env=wine_env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=90, check=True)
        with tempfile.TemporaryDirectory(prefix="crystalcast-wine-consumer-") as folder:
            executable = Path(folder) / "wine-frame-consumer.exe"
            shutil.copy2(args.consumer, executable)
            for name in ("d3d11.dll", "dxgi.dll"):
                shutil.copy2(args.dxvk_dir / name, Path(folder) / name)
            for mode in ("upload", "verify") if args.mode == "both" else (args.mode,):
                print("Running " + mode + "; keep both windows visible.", flush=True)
                report["passes"][mode] = run_pass(args, mode, wine_env, executable)
    except KeyboardInterrupt:
        report["errors"].append("Cancelled by user")
    except Exception:
        report["errors"].append("Wine initialization or harness execution did not complete; check the dedicated runner and runtime dependencies")
    finally:
        server = Path(args.wine).parent / "wineserver"
        if server.is_file():
            try:
                subprocess.run([str(server), "-k"], env=wine_env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
                subprocess.run([str(server), "-w"], env=wine_env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
            except (OSError, subprocess.TimeoutExpired):
                report["errors"].append("Dedicated Wine server cleanup did not finish")
        prefix_lock.close()
    (args.output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
    lines = ["CrystalCast native GTK / Wine / DXVK experiment", report["scope"], "Wine: " + report["wine_version"], ""]
    for mode, result in report["passes"].items():
        lines += [mode + ": " + result["status"], "  DXVK: " + str(result["dxvk_version"]),
                  "  Adapter: " + result["consumer"].get("adapter", "unavailable"),
                  "  Acceleration: " + result["consumer_acceleration"],
                  "  Source-verified unique fps: " + str(result["source_verified_unique_fps"]),
                  "  Every frame verified after upload: " + str(result["every_frame_verified_after_d3d11_upload"]),
                  "  Complete-path verified unique fps: " + str(result["complete_path_verified_unique_fps"]),
                  "  Sustained verified 1080p60: " + str(result["sustained_verified_1080p60"]),
                  "  Repeated / skipped / backpressure: " + str((result["repeated_frames"], result["skipped_source_frames"], result["backpressure_capture_drops"])),
                  "  Counter resets/reordering / ambiguous: " + str((result["counter_resets_or_reordering"], result["counter_ambiguous"])),
                  "  Capture start -> Present ack (us): " + str(result["capture_start_to_present_ack_us"]),
                  "  CPU percent / summed peak RSS MiB: " + str((result["benchmark_tree_cpu_percent"], result["benchmark_tree_peak_summed_rss_mib"])),
                  "  Native extraction (us): " + str(result["native_extraction_us"])]
        lines += ["  " + key + ": " + str(value) for key, value in result["consumer_timings_us"].items()]
        lines += result["errors"] + ["  Consumer exception: " + str(result.get("consumer_exception")), ""]
    lines += report["errors"] + [report["timing_notes"], report["resource_notes"], "Nothing uploaded. Share summary.txt and results.json after review."]
    (args.output / "summary.txt").write_text("\n".join(lines) + "\n")
    print("Finished. Local results: " + str(args.output))
    return 0 if report["passes"] and not report["errors"] and all(x["status"] == "ok" for x in report["passes"].values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
