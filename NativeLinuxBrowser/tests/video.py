#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import time
from fixtures import VideoFixture
from smoke import Client
from resources import Resources

parser = argparse.ArgumentParser()
parser.add_argument("--helper", default="build/crystalcast-browser-helper")
parser.add_argument("--fixture", default="artifacts/test-h264-aac.mp4")
parser.add_argument("--script", default="artifacts/generic-controller.js")
parser.add_argument("--transport", choices=["tcp", "shared-file"], default="shared-file")
parser.add_argument("--width", type=int, default=1280)
parser.add_argument("--height", type=int, default=720)
parser.add_argument("--video-width", type=int, default=1280)
parser.add_argument("--seconds", type=float, default=12)
parser.add_argument("--mse", action="store_true")
parser.add_argument("--mse-codecs", default="avc1.42c020,mp4a.40.2")
parser.add_argument("--output", default="artifacts/video.json")
args = parser.parse_args()
fixture = VideoFixture(args.fixture, args.mse, args.mse_codecs)
runtime = fixture.directory / "runtime"
runtime.mkdir(mode=0o700)
session = secrets.token_hex(16)
endpoint = runtime / "crystalcast" / session / "endpoint.json"
client = None
with (fixture.directory / "helper.log").open("wb") as log:
    environment = dict(os.environ, GST_DEBUG=os.environ.get("GST_DEBUG", "2"))
    process = subprocess.Popen([str(Path(args.helper).resolve()), "--runtime-root", str(runtime), "--session", session], stdout=log, stderr=log, env=environment)
    try:
        deadline = time.monotonic() + 15
        while not endpoint.exists():
            if process.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError((fixture.directory / "helper.log").read_text())
            time.sleep(.05)
        client = Client(endpoint, args.transport)
        client.send(type="load", uri=fixture.url, generic=True, script=Path(args.script).read_text(),
                    width=args.width, height=args.height, fps=60)
        frames, hashes, messages, evaluations, latencies, frame_times = 0, set(), [], [], [], []
        resources = Resources(process.pid)
        started = time.monotonic()
        last_evaluation = 0
        first = None
        while time.monotonic() - started < args.seconds:
            client.ping()
            if time.monotonic() - last_evaluation > 1:
                resources.sample()
                client.send(type="evaluate", id=1, script="(()=>{const v=document.querySelector('video');return {time:v.currentTime,duration:v.duration,ready:v.readyState,paused:v.paused,network:v.networkState,error:v.error?.message,width:v.videoWidth,height:v.videoHeight,quality:v.getVideoPlaybackQuality?.()}})()")
                last_evaluation = time.monotonic()
            kind, value = client.receive()
            if kind == "frame":
                metadata, pixels = value
                frames += 1
                frame_times.append(time.monotonic() - started)
                hashes.add(hashlib.sha256(pixels[::4096]).hexdigest())
                first = first or time.monotonic() - started
                latencies.append(time.time() * 1000 - metadata["timestamp"])
            else:
                messages.append(value)
                if value["type"] == "message" and json.loads(value["message"]).get("type") == "ready":
                    client.send(type="command", message=json.dumps(dict(type="settings", nonce="native-video-test", settings=dict(audioEnabled=False, volume=.7, playbackRate=1, loop=True, playlistAutoplayNext=False))))
                    client.send(type="command", message=json.dumps(dict(type="play", nonce="native-video-test")))
                if value["type"] == "evaluation":
                    evaluations.append(json.loads(value["result"]))
        elapsed = time.monotonic() - started
        resources.sample()
        usage = resources.result()
        client.close()
        client = None
        process.wait(timeout=10)
        latencies.sort()
        intervals = sorted((b - a) * 1000 for a, b in zip(frame_times, frame_times[1:]) if a >= 1)
        metrics = next((m for m in reversed(messages) if m["type"] == "metrics"), {})
        result = dict(transport=args.transport, width=args.width, height=args.height, mse=args.mse,
                      frames=frames, changed_samples=len(hashes), received_fps=frames/elapsed,
                      first_frame_seconds=first, evaluations=evaluations, messages=messages,
                      mean_export_to_receive_ms=sum(latencies)/max(1, len(latencies)), log=str(fixture.directory / "helper.log"),
                      p95_export_to_receive_ms=latencies[int((len(latencies)-1)*.95)] if latencies else None,
                      p95_frame_interval_ms=intervals[int((len(intervals)-1)*.95)] if intervals else None,
                      steady_received_fps=sum(t >= 1 for t in frame_times)/max(.001, elapsed-1),
                      helper_metrics=metrics, **usage, session_cleaned=not endpoint.parent.exists())
        Path(args.output).write_text(json.dumps(result, indent=2))
        print(json.dumps({k: v for k, v in result.items() if k != "messages"}, indent=2))
        assert frames > 10 and len(hashes) > 5, "No sustained changing video pixels"
        assert any(e.get("width") == args.video_width and e.get("time", 0) > 2 and e.get("duration", 0) >= 8
                   and e.get("ready", 0) >= 3 for e in evaluations if isinstance(e, dict)), "Decoded playback was not established"
        assert result["session_cleaned"] and process.returncode == 0
    finally:
        if client:
            client.close()
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
        fixture.close()
