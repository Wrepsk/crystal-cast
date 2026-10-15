#!/usr/bin/env python3
"""Start a helper and local H.264/AAC fixture for the C# probe.

Prints paths/URL only, never the IPC credential. Use podman cp to copy the
endpoint into a private Windows temp directory for cross-WSL development.
Normal Wine users need neither this harness nor an endpoint override.
"""
import argparse
import json
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
from fixtures import VideoFixture

parser = argparse.ArgumentParser()
parser.add_argument("--fixture", default="artifacts/test-h264-aac.mp4")
parser.add_argument("--mse", action="store_true")
args = parser.parse_args()
fixture = VideoFixture(args.fixture, args.mse)
directory = fixture.directory
runtime = directory / "runtime"
runtime.mkdir(mode=0o700)
session = secrets.token_hex(16)
endpoint = runtime / "crystalcast" / session / "endpoint.json"
with (directory / "helper.log").open("wb") as log:
    process = subprocess.Popen([str(Path("build/crystalcast-browser-helper").resolve()), "--runtime-root", str(runtime), "--session", session], stdout=log, stderr=log)
    try:
        deadline = time.monotonic() + 10
        while not endpoint.exists():
            if process.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError((directory / "helper.log").read_text())
            time.sleep(.05)
        print(json.dumps(dict(endpoint=str(endpoint), url=fixture.url, log=str(directory / "helper.log"))), flush=True)
        process.wait(timeout=120)
        print(json.dumps(dict(exit=process.returncode, session_cleaned=not endpoint.parent.exists())), flush=True)
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
        fixture.close()
