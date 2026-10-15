#!/usr/bin/env python3
"""Exercise real headless WebKit frames; no game, Wine, or screenshot API."""
import argparse
import hashlib
import json
import mmap
import os
from pathlib import Path
import secrets
import socket
import struct
import subprocess
import tempfile
import time

MAGIC = 0x31424343


class Client:
    def __init__(self, endpoint, transport="tcp"):
        description = json.loads(Path(endpoint).read_text())
        self.socket = socket.create_connection(("127.0.0.1", description["port"]), timeout=15)
        self.socket.settimeout(15)
        self.mapping = None
        self.mapping_file = None
        if transport == "shared-file":
            self.mapping_file = open(Path(endpoint).parent / "frames.bin", "rb")
            self.mapping = mmap.mmap(self.mapping_file.fileno(), 0, access=mmap.ACCESS_READ)
        self.send(type="hello", version=1, token=description["token"], transport=transport)
        self.hello = self.receive()[1]
        assert self.hello["type"] == "hello", self.hello
        self.last_ping = time.monotonic()

    def send(self, **message):
        raw = json.dumps(message, separators=(",", ":")).encode()
        self.socket.sendall(struct.pack("<IIII", MAGIC, 1, len(raw), 0) + raw)

    def exact(self, length):
        result = bytearray(length)
        position = 0
        while position < length:
            count = self.socket.recv_into(memoryview(result)[position:])
            if not count:
                raise EOFError("helper disconnected")
            position += count
        return result

    def receive(self, acknowledge=True):
        magic, kind, length, reserved = struct.unpack("<IIII", self.exact(16))
        assert magic == MAGIC and reserved == 0
        assert length <= 1920 * 1080 * 4 + 32
        payload = self.exact(length)
        if kind == 1:
            message = json.loads(payload)
            if message["type"] == "frame":
                count = message["width"] * message["height"] * 4
                offset = message["slot"] * self.hello["slotSize"]
                pixels = self.mapping[offset:offset + count]
                if acknowledge:
                    self.send(type="ack", slot=message["slot"], sequence=message["sequence"])
                return "frame", (message, pixels)
            return "json", message
        assert kind == 2
        width, height, stride, fmt, sequence, timestamp = struct.unpack_from("<IIIIQq", payload)
        assert fmt == 1 and stride == width * 4 and length == 32 + stride * height
        return "frame", ({"width": width, "height": height, "sequence": sequence, "timestamp": timestamp}, payload[32:])

    def ping(self):
        if time.monotonic() - self.last_ping > 2:
            self.send(type="ping")
            self.last_ping = time.monotonic()

    def close(self):
        try:
            self.send(type="shutdown")
        except OSError:
            pass
        self.socket.close()
        if self.mapping:
            self.mapping.close()
            self.mapping_file.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--helper", default="build/crystalcast-browser-helper")
    parser.add_argument("--transport", choices=["tcp", "shared-file"], default="tcp")
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=360)
    parser.add_argument("--seconds", type=float, default=5)
    parser.add_argument("--output", default="artifacts/smoke.json")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()
    runtime = tempfile.mkdtemp(prefix="crystalcast-native-")
    session = secrets.token_hex(16)
    directory = Path(runtime) / "crystalcast" / session
    log_path = Path(runtime) / "helper.log"
    client = None
    with log_path.open("wb") as log:
        process = subprocess.Popen([str(Path(args.helper).resolve()), "--session", session, "--runtime-root", runtime], stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + 15
            endpoint = directory / "endpoint.json"
            while not endpoint.exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(log_path.read_text())
                time.sleep(.05)
            client = Client(endpoint, args.transport)
            client.send(type="probe")
            while True:
                kind, probe = client.receive()
                if kind == "json" and probe["type"] == "probe":
                    assert probe["protocol"] == 1 and (probe["major"], probe["minor"]) >= (2, 54)
                    break
            html = """<!doctype html><html><body style='margin:0;background:#ff0000'><canvas id='c'></canvas><script>
const c=document.getElementById('c');c.width=innerWidth;c.height=innerHeight;const ctx=c.getContext('2d');let n=0;
function draw(){ctx.fillStyle=(n++%2)?'#ff0000':'#0000ff';ctx.fillRect(0,0,c.width,c.height);requestAnimationFrame(draw)};draw();
window.chrome.webview.postMessage({type:'probe',value:42,origin:location.origin,mse:typeof MediaSource!=='undefined',h264:document.createElement('video').canPlayType('video/mp4; codecs="avc1.42E01E,mp4a.40.2"')});
</script></body></html>"""
            client.send(type="load", uri="https://crystalcast.local/player/smoke.html", html=html,
                        width=args.width, height=args.height, fps=60, generic=False)
            frames, hashes, messages, export_metrics, opaque = 0, set(), [], {}, 0
            first = None
            started = time.monotonic()
            probe_seen = evaluation_seen = False
            while time.monotonic() - started < args.seconds:
                client.ping()
                kind, message = client.receive()
                if args.verbose:
                    print(kind, message if kind == "json" else message[0], flush=True)
                if kind == "frame":
                    info, pixels = message
                    assert (info["width"], info["height"]) == (args.width, args.height), info
                    assert len(pixels) == args.width * args.height * 4
                    opaque += int(pixels[3] == 255)
                    frames += 1
                    hashes.add(hashlib.sha256(pixels).hexdigest())
                    if first is None:
                        first = time.monotonic() - started
                        client.send(type="evaluate", script="21*2", id=7)
                else:
                    messages.append(message)
                    if message["type"] == "message":
                        probe = json.loads(message["message"])
                        probe_seen |= probe.get("type") == "probe" and probe.get("value") == 42
                    if message["type"] == "evaluation":
                        evaluation_seen |= message.get("id") == 7 and message.get("result") == "42"
                    if message["type"] == "metrics":
                        export_metrics = message
            elapsed = time.monotonic() - started
            assert frames >= 2 and len(hashes) >= 2 and opaque >= 2, (frames, hashes, messages)
            assert probe_seen and evaluation_seen, messages
            client.close()
            client = None
            process.wait(timeout=10)
            assert process.returncode == 0, log_path.read_text()
            assert not directory.exists(), "helper did not remove its per-screen runtime directory"
            result = dict(engine="WPE", transport=args.transport, width=args.width, height=args.height,
                          frames=frames, changed_frames=len(hashes), elapsed_seconds=elapsed,
                          received_fps=frames/elapsed, first_frame_seconds=first,
                          metrics=export_metrics, messages=messages, stderr=log_path.read_text())
            output = Path(args.output)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(result, indent=2))
            print(json.dumps({k: v for k, v in result.items() if k not in ("messages", "stderr")}, indent=2))
        finally:
            if client:
                client.close()
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            if process.returncode:
                print(log_path.read_text())
            # The helper owns and removes its files. Preserve any failure log in
            # the OS temp directory so diagnostics survive a test failure.


if __name__ == "__main__":
    main()
