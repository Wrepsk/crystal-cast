#!/usr/bin/env python3
"""Exercise the real native IPC boundary, lease ownership and failure cleanup."""
import json
from pathlib import Path
import secrets
import socket
import stat
import struct
import subprocess
import tempfile
import time
from smoke import Client, MAGIC


def descendants(pid):
    found, pending = set(), [pid]
    while pending:
        parent = pending.pop()
        try:
            child_pids = {int(p) for p in Path(f"/proc/{parent}/task/{parent}/children").read_text().split()}
            pending.extend(child_pids - found)
            found.update(child_pids)
        except FileNotFoundError:
            pass
    return found


class Helper:
    def __init__(self):
        self.root = Path(tempfile.mkdtemp(prefix="crystalcast-lifecycle-"))
        self.directory = self.root / "crystalcast" / secrets.token_hex(16)
        self.endpoint = self.directory / "endpoint.json"
        self.log = (self.root / "helper.log").open("wb")
        self.process = subprocess.Popen([str(Path("build/crystalcast-browser-helper").resolve()),
            "--runtime-root", str(self.root), "--session", self.directory.name], stdout=self.log, stderr=self.log)
        deadline = time.monotonic() + 15
        while not self.endpoint.exists():
            assert self.process.poll() is None and time.monotonic() < deadline, "Helper startup failed"
            time.sleep(.05)

    def stopped(self, child_pids=(), timeout=10):
        self.process.wait(timeout=timeout)
        self.log.close()
        assert self.process.returncode == 0, (self.root / "helper.log").read_text()
        assert not self.directory.exists(), "Session resources leaked"
        assert not any(Path(f"/proc/{pid}").exists() for pid in child_pids), "Browser children leaked"


helper = Helper()
description = json.loads(helper.endpoint.read_text())
assert stat.S_IMODE(helper.directory.stat().st_mode) == 0o700
for path in (helper.endpoint, helper.directory / "frames.bin"):
    assert stat.S_IMODE(path.stat().st_mode) == 0o600
wrong = socket.create_connection(("127.0.0.1", description["port"]), timeout=5)
message = json.dumps(dict(type="hello", version=1, token="0" * 64)).encode()
wrong.sendall(struct.pack("<IIII", MAGIC, 1, len(message), 0) + message)
assert wrong.recv(1) == b"", "Unauthenticated client was accepted"
wrong.close()

client = Client(helper.endpoint, "shared-file")
client.send(type="load", uri="https://crystalcast.local/player/test.html", generic=False,
    html="""<canvas id='c'></canvas><script>const c=document.querySelector('canvas');
c.width=640;c.height=360;let n=0;function f(){let x=c.getContext('2d');x.fillStyle=`rgb(${n++%255},0,0)`;
x.fillRect(0,0,640,360);requestAnimationFrame(f)}f();</script>""", width=640, height=360, fps=60)
leased = []
while len(leased) < 3:
    kind, value = client.receive(acknowledge=False)
    if kind == "frame":
        leased.append(value)
assert len({m["slot"] for m, _ in leased}) == 3
deadline, dropped = time.monotonic() + 2, 0
while time.monotonic() < deadline:
    client.ping()
    kind, value = client.receive(acknowledge=False)
    assert kind != "frame", "A leased slot was reused without acknowledgement"
    if value["type"] == "metrics":
        dropped = value["dropped"]
assert dropped > 0, "Slow consumer did not exercise bounded frame dropping"
for metadata, pixels in leased:
    offset = metadata["slot"] * client.hello["slotSize"]
    assert client.mapping[offset:offset + len(pixels)] == pixels, "Leased pixels were overwritten"
first = leased[0][0]
client.send(type="ack", slot=first["slot"], sequence=first["sequence"] + 999)
kind, _ = client.receive(acknowledge=False)
assert kind != "frame", "Incorrect sequence released a slot"
client.send(type="ack", slot=first["slot"], sequence=first["sequence"])
while True:
    kind, value = client.receive(acknowledge=False)
    if kind == "frame":
        assert value[0]["slot"] == first["slot"] and value[0]["sequence"] > first["sequence"]
        break
child_pids = descendants(helper.process.pid)
assert child_pids, "No browser process tree was exercised"
client.socket.close()  # Abrupt EOF, deliberately no shutdown command.
client.mapping.close()
client.mapping_file.close()
helper.stopped(child_pids)
print("PASS: authentication, private files, bounded leases, wrong ACK rejection, EOF and child cleanup", flush=True)

helper = Helper()
client = Client(helper.endpoint)
client.socket.sendall(struct.pack("<IIII", MAGIC, 1, 512 * 1024 + 1, 0))
helper.stopped()
client.socket.close()
print("PASS: oversized authenticated packet rejected and session cleaned", flush=True)

helper = Helper()
helper.stopped(timeout=25)
print("PASS: unconnected startup expires and cleans resources", flush=True)
