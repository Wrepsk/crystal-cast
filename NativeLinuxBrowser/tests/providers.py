#!/usr/bin/env python3
"""Record provider behavior; a loaded page is explicitly NOT a playback pass."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
from smoke import Client

parser = argparse.ArgumentParser()
parser.add_argument("--pages", default="artifacts/providers")
parser.add_argument("--seconds", type=float, default=20)
parser.add_argument("--output", default="artifacts/providers.json")
parser.add_argument("--provider")
args = parser.parse_args()
results = {}
for page in sorted(Path(args.pages).glob("*.json")):
    if args.provider and page.stem != args.provider:
        continue
    runtime = Path(tempfile.mkdtemp(prefix="crystalcast-provider-"))
    session = secrets.token_hex(16)
    endpoint = runtime / "crystalcast" / session / "endpoint.json"
    client = None
    with (runtime / "helper.log").open("wb") as log:
        process = subprocess.Popen([str(Path("build/crystalcast-browser-helper").resolve()), "--runtime-root", str(runtime), "--session", session], stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + 10
            while not endpoint.exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError("helper startup failed")
                time.sleep(.05)
            client = Client(endpoint, "shared-file")
            request = json.loads(page.read_text())
            client.send(**request)
            started = time.monotonic()
            frames, hashes, messages, telemetry = 0, set(), [], []
            while time.monotonic() - started < args.seconds:
                client.ping()
                kind, value = client.receive()
                if kind == "frame":
                    frames += 1
                    hashes.add(hashlib.sha256(value[1][::4096]).hexdigest())
                else:
                    messages.append(value)
                    if value["type"] == "message":
                        message = json.loads(value["message"])
                        if message["type"] == "ready":
                            nonce = message.get("nonce")
                            client.send(type="command", message=json.dumps(dict(type="settings", nonce=nonce, settings=dict(audioEnabled=False, volume=.7, playbackRate=1, loop=False, playlistAutoplayNext=False))))
                            client.send(type="command", message=json.dumps(dict(type="play", nonce=nonce)))
                        if message["type"] == "status":
                            telemetry.append(message)
            playing = [t for t in telemetry if t.get("state") == 1 and t.get("positionSeconds", 0) > 2]
            results[page.stem] = dict(frames=frames, changed_samples=len(hashes), telemetry=telemetry, messages=messages,
                                      playback_evidence=bool(playing and len(hashes) > 5), log=str(runtime / "helper.log"))
            client.close()
            client = None
            process.wait(timeout=10)
            results[page.stem]["session_cleaned"] = not endpoint.parent.exists()
        except Exception as error:
            results[page.stem] = dict(error=str(error), log=str(runtime / "helper.log"))
        finally:
            if client:
                client.close()
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
    print(page.stem, {k: v for k, v in results[page.stem].items() if k not in ("messages", "telemetry")}, flush=True)
    Path(args.output).write_text(json.dumps(results, indent=2))
