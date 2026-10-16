#!/usr/bin/env python3
"""Local video fixture, leased mmap consumer, and extraction benchmark (Linux only)."""
import argparse
import collections
import functools
import http.server
import json
import math
import mmap
import os
from pathlib import Path
import socket
import statistics
import struct
import subprocess
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parent
WIDTH, HEIGHT = 1920, 1080
FRAME_BYTES = WIDTH * HEIGHT * 4


def fixture(directory):
    video = directory / "video.mp4"
    if not video.exists():
        filters = ["drawbox=x=0:y=0:w=960:h=64:color=black:t=fill"]
        filters += [f"drawbox=x={bit * 96}:y=0:w=80:h=48:color=white:t=fill:"
                    f"enable='mod(floor(n/{2**bit}),2)'" for bit in range(10)]
        # Channel/orientation reference bars are encoded in the video itself.
        filters += [f"drawbox=x={x}:y=1000:w=160:h=80:color={color}:t=fill"
                    for x, color in [(0, "red"), (160, "lime"), (320, "blue")]]
        subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i",
                        "testsrc2=size=1920x1080:rate=60", "-t", "45", "-vf",
                        ",".join(filters), "-c:v", "libx264", "-preset", "veryfast",
                        "-crf", "23", "-pix_fmt", "yuv420p", "-threads", "4",
                        "-movflags", "+faststart", str(video)], check=True)
    return video


def page(pause, overlay):
    return """<!doctype html><meta name=viewport content="width=device-width,initial-scale=1">
<style>html,body{margin:0;background:black;overflow:hidden}video{width:1920px;height:1080px;display:block}
#overlay{position:absolute;left:1500px;top:800px;background:#00ffff;color:#000;font:32px sans-serif;padding:16px}</style>
<video id=v autoplay muted loop playsinline src=/video.mp4></video>OVERLAY
<script>
const v=document.querySelector('video'); let callbacks=0,lastMediaTime=0;
function frame(t,m){callbacks++;lastMediaTime=m.mediaTime;v.requestVideoFrameCallback(frame)}
if(v.requestVideoFrameCallback)v.requestVideoFrameCallback(frame);
function report(event){const q=v.getVideoPlaybackQuality();
 window.webkit.messageHandlers.probe.postMessage(JSON.stringify({event,time:v.currentTime,
 width:v.videoWidth,height:v.videoHeight,paused:v.paused,ready:v.readyState,
 decoded:q.totalVideoFrames,dropped:q.droppedVideoFrames,callbacks,lastMediaTime,
 error:v.error&&v.error.message}));}
['playing','pause','seeked','error'].forEach(e=>v.addEventListener(e,()=>report(e)));
setInterval(()=>report('interval'),500);
v.play().catch(e=>report(String(e))); PAUSE
</script>""".replace("PAUSE", "setTimeout(()=>v.pause(),8000);setTimeout(()=>v.play(),10000);" if pause else "").replace(
        "OVERLAY", "<div id=overlay>GTK composition</div>" if overlay else "")


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        if self.path != "/video.mp4":
            return super().do_GET()
        path = Path(self.directory) / "video.mp4"
        size = path.stat().st_size
        start, end, status = 0, size - 1, 200
        requested = self.headers.get("Range")
        if requested:
            import re
            match = re.fullmatch(r"bytes=(\d+)-(\d*)", requested)
            if not match:
                self.send_error(416)
                return
            start = int(match[1])
            end = min(int(match[2]) if match[2] else size - 1, size - 1)
            if start > end:
                self.send_error(416)
                return
            status = 206
        self.send_response(status)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        try:
            with path.open("rb") as source:
                source.seek(start)
                remaining = end - start + 1
                while remaining:
                    block = source.read(min(remaining, 65536))
                    self.wfile.write(block)
                    remaining -= len(block)
        except (BrokenPipeError, ConnectionResetError):
            pass  # Browser seeking or closing a stream is expected.

    def log_message(self, *_args):
        pass


def process_tree(root_pid):
    """Sample live tree. Cumulative ticks retain exited PIDs; RSS may double-count sharing."""
    processes = {}
    for path in Path("/proc").glob("[0-9]*/stat"):
        try:
            text = path.read_text()
            fields = text[text.rindex(")") + 2:].split()
            processes[int(path.parent.name)] = (int(fields[1]),
                                                int(fields[11]) + int(fields[12]),
                                                int(fields[21]) * os.sysconf("SC_PAGE_SIZE"))
        except (OSError, ValueError, IndexError):
            continue
    selected = {root_pid}
    while True:
        children = {pid for pid, (parent, _, _) in processes.items() if parent in selected}
        if children <= selected:
            break
        selected |= children
    return {pid: processes[pid] for pid in selected if pid in processes}


def distribution(values):
    values = sorted(values)
    return {"mean": round(statistics.mean(values), 3),
            "p95": round(values[max(0, math.ceil(len(values) * .95) - 1)], 3),
            "max": round(max(values), 3)} if values else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=["texture", "direct", "shm", "render-shm"], default="texture")
    parser.add_argument("--seconds", type=int, default=20)
    parser.add_argument("--warmup", type=int, default=5)
    parser.add_argument("--audit-every", type=int, default=60)
    parser.add_argument("--consumer-delay-ms", type=int, default=0)
    parser.add_argument("--hide-after", type=int, default=0)
    parser.add_argument("--exercise-pause", action="store_true")
    parser.add_argument("--overlay", action="store_true")
    parser.add_argument("--software-webkit", action="store_true")
    parser.add_argument("--poll-hz", type=int, default=0)
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--software-web-process", action="store_true")
    parser.add_argument("--png", action="store_true", help="Diagnostic run; introduces extra readback")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    media_dir = ROOT / "artifacts" / "fixture-v2"
    media_dir.mkdir(parents=True, exist_ok=True)
    fixture(media_dir)
    (media_dir / "index.html").write_text(page(args.exercise_pause, args.overlay))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0),
        functools.partial(Handler, directory=str(media_dir)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    consumed, consumer_errors, ticks, rss = [], [], {}, []
    stop_sampler = threading.Event()
    shm_mode = args.mode in ("shm", "render-shm")
    with tempfile.TemporaryDirectory(prefix="crystalcast-gtk-") as private:
        shm_path = Path(private) / "frames.bgra"
        parent, child = socket.socketpair(socket.AF_UNIX, socket.SOCK_SEQPACKET)
        command = [str(ROOT / "build/gtk-frame-probe"), "--uri", f"http://127.0.0.1:{server.server_port}/",
                   "--mode", args.mode, "--seconds", str(args.seconds), "--warmup", str(args.warmup),
                   "--audit-every", str(args.audit_every), "--hide-after", str(args.hide_after),
                   "--poll-hz", str(args.poll_hz)]
        if args.png:
            command += ["--png", str(args.output / "first.png"), "--node", str(args.output / "first.node")]
        if args.software_webkit:
            command += ["--software-webkit"]
        if args.preview:
            command += ["--preview"]
        if args.software_web_process:
            command += ["--software-web-process"]
        if shm_mode:
            command += ["--shm", str(shm_path), "--ack-fd", str(child.fileno())]
        with (args.output / "stderr.log").open("w") as log:
            process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=log, text=True,
                                       pass_fds=(child.fileno(),) if shm_mode else ())
            child.close()

            def consume():
                try:
                    with shm_path.open("rb") as shared, mmap.mmap(shared.fileno(), 3 * FRAME_BYTES, access=mmap.ACCESS_READ) as pixels:
                        while True:
                            try:
                                notice = parent.recv(24)
                            except ConnectionResetError:
                                break  # Producer can close with final ACKs still queued.
                            if not notice:
                                break
                            seq, slot, size, timestamp = struct.unpack("<QIIq", notice)
                            if slot >= 3 or size != FRAME_BYTES:
                                raise ValueError("bad frame metadata")
                            # Full copy models the existing pooled VideoFrame consumer.
                            start = time.monotonic_ns()
                            image = pixels[slot * size:(slot + 1) * size]
                            marker = sum((image[24 * WIDTH * 4 + (bit * 96 + 40) * 4] > 127) << bit for bit in range(10))
                            bars = [tuple(image[(1040 * WIDTH + x) * 4:(1040 * WIDTH + x) * 4 + 4]) for x in (80, 240, 400)]
                            overlay_pixel = tuple(image[(810 * WIDTH + 1510) * 4:(810 * WIDTH + 1510) * 4 + 4])
                            end = time.monotonic_ns()
                            consumed.append({"seq": seq, "marker": marker, "bars_bgra": bars,
                                             "overlay_bgra": overlay_pixel,
                                             "copy_us": (end - start) / 1000,
                                             "age_us": end / 1000 - timestamp})
                            if args.consumer_delay_ms:
                                time.sleep(args.consumer_delay_ms / 1000)
                            try:
                                parent.sendall(struct.pack("<Q", seq))
                            except (BrokenPipeError, ConnectionResetError):
                                pass  # Drain any final published leases after producer exit.
                except Exception as error:
                    consumer_errors.append(repr(error))

            consumer = None
            if shm_mode:
                deadline = time.monotonic() + 10
                while ((not shm_path.exists() or shm_path.stat().st_size != 3 * FRAME_BYTES)
                       and process.poll() is None and time.monotonic() < deadline):
                    time.sleep(.01)
                if shm_path.exists():
                    consumer = threading.Thread(target=consume, daemon=True)
                    consumer.start()
                else:
                    consumer_errors.append("shared file was not created")

            def sample():
                while not stop_sampler.is_set():
                    now = time.monotonic_ns() / 1000
                    tree = process_tree(process.pid)
                    ticks.update({pid: data[1] for pid, data in tree.items()})
                    rss.append((now, sum(data[2] for data in tree.values()), sum(ticks.values())))
                    stop_sampler.wait(.1)

            sampler = threading.Thread(target=sample, daemon=True)
            sampler.start()
            events = []
            watchdog = threading.Timer(args.warmup + args.seconds + 30, process.kill)
            watchdog.start()
            try:
                with (args.output / "events.jsonl").open("w") as raw:
                    for line in process.stdout:
                        raw.write(line)
                        events.append(json.loads(line))
                code = process.wait(timeout=5)
            finally:
                watchdog.cancel()
                stop_sampler.set()
                sampler.join()
                if process.poll() is None:
                    process.kill()
                    process.wait()
                if consumer:
                    consumer.join(timeout=5)
                parent.close()
                server.shutdown()
    (args.output / "consumer.jsonl").write_text("".join(json.dumps(x) + "\n" for x in consumed))
    frames = [event for event in events if event["kind"] == "frame"]
    starts = [event for event in events if event["kind"] == "start"]
    media = [event for event in events if event["kind"] == "media"]
    baseline = starts[0]["time_us"] + args.warmup * 1e6 if starts else 0
    ends = [x for x in events if x["kind"] == "end"]
    measured_duration = (ends[-1]["time_us"] - baseline) / 1e6 if ends else args.seconds
    measured = [sample for sample in rss if sample[0] >= baseline]
    duration = args.seconds
    markers = [x["marker"] for x in frames if x["downloaded"]]
    changes = sum(a != b for a, b in zip(markers, markers[1:]))
    deltas = [(b - a) % 1024 for a, b in zip(markers, markers[1:])]
    intervals = [(b["time_us"] - a["time_us"]) / 1000 for a, b in zip(frames, frames[1:])]
    cpu = (measured[-1][2] - measured[0][2]) / os.sysconf("SC_CLK_TCK") / ((measured[-1][0] - measured[0][0]) / 1e6) * 100 if len(measured) > 1 else None
    def correct_bars(x):
        red, green, blue = x["bars_bgra"]
        return (red[2] > 180 and red[0] < 40 and green[1] > 180 and green[0] < 40
                and blue[0] > 180 and blue[2] < 40 and all(bar[3] == 255 for bar in (red, green, blue)))
    bad_bars = sum(not correct_bars(x) for x in consumed)
    bars_ok = bad_bars == 0
    producer_markers = {x["seq"]: x["marker"] for x in frames}
    mismatch = sum(producer_markers.get(x["seq"]) != x["marker"] for x in consumed)
    overlay_ok = all(x["overlay_bgra"] == (255, 255, 0, 255) for x in consumed) if args.overlay and consumed else None
    summary = {
        "options": vars(args) | {"output": str(args.output)}, "environment": starts,
        "exit_code": code, "frames": len(frames), "fps_fixed_window": len(frames) / duration,
        "measured_window_seconds": measured_duration,
        "fps_measured_window": len(frames) / measured_duration,
        "frame_interval_ms": distribution(intervals),
        "extraction_us": {key: distribution([x[key] for x in frames]) for key in
                          ("snapshot_us", "render_us", "download_us", "release_us", "total_us")},
        "unaudited_total_us": distribution([x["total_us"] for x in frames if not x["downloaded"]]),
        "output_textures": dict(collections.Counter(x["texture"] for x in frames)),
        "source_textures": dict(collections.Counter(x["source"] for x in frames)),
        "direct_frames": sum(x["direct"] for x in frames), "marker_audits": len(markers),
        "marker_changes": changes, "distinct_markers": len(set(markers)),
        "adjacent_marker_repeats": sum(delta == 0 for delta in deltas),
        "marker_step_distribution": distribution(deltas),
        "changed_frame_fps": changes / measured_duration if len(markers) == len(frames) else None,
        "consumer_frames": len(consumed), "consumer_errors": consumer_errors,
        "consumer_copy_us": distribution([x["copy_us"] for x in consumed]),
        "consumer_age_us": distribution([x["age_us"] for x in consumed]),
        "channel_bars_ok": bars_ok if consumed else None,
        "channel_bar_failures": bad_bars, "consumer_marker_mismatches": mismatch,
        "overlay_ok": overlay_ok,
        "tree_cpu_percent": round(cpu, 2) if cpu is not None else None,
        "tree_rss_peak_mib": round(max(x[1] for x in measured) / 1024**2, 2) if measured else None,
        "media_first": media[0] if media else None, "media_last": media[-1] if media else None,
        "end": ends,
    }
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    media_ok = media and any(x["data"]["time"] > 1 and x["data"]["width"] == WIDTH and
                            x["data"]["height"] == HEIGHT for x in media) and not any(x["data"]["error"] for x in media)
    if (code or not frames or consumer_errors or (len(markers) > 1 and changes < 1) or
            (consumed and (not bars_ok or mismatch or len(consumed) != len(frames))) or
            overlay_ok is False or not media_ok):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
