#!/usr/bin/env python3
"""Developer-only tests of corrupted/unsupported runtime startup, no game."""
from pathlib import Path
import argparse
import subprocess
import tempfile
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("archive")
args = parser.parse_args()
with tempfile.TemporaryDirectory(prefix="crystalcast-failure-") as temporary:
    root = Path(temporary)
    with zipfile.ZipFile(args.archive) as archive:
        archive.extractall(root)
        for item in archive.infolist():
            (root / item.filename).chmod(0o755 if item.external_attr >> 16 & 0o111 else 0o644)
    runtime = root / "session"
    runtime.mkdir(mode=0o700)
    command = [str(root / "bin/crystalcast-browser"), "--runtime-root", str(runtime), "--session", "a" * 32]
    # A valid root/loader cannot compensate for a missing engine. Run in a
    # clean target without system WPE so loader fallback cannot hide corruption.
    engines = list((root / "lib").glob("libWPEWebKit-2.0.so.*"))
    assert len(engines) == 1, "Expected one WebKit SONAME in the package"
    engine = engines[0]
    original = engine.with_suffix(".saved")
    engine.rename(original)
    result = subprocess.run(command, capture_output=True, text=True, timeout=10)
    assert result.returncode and engine.name in result.stderr, result.stderr
    assert not list(runtime.iterdir()), "Failed dependency check published IPC state"
    original.rename(engine)
    # The clean target has no system bwrap; replace only the test copy's tool.
    if Path("/usr/bin/bwrap").exists():
        raise RuntimeError("Sandbox failure test needs a clean target without system bubblewrap")
    (root / "libexec/bwrap").write_text("#!/bin/sh\nexit 1\n")
    (root / "libexec/bwrap").chmod(0o755)
    result = subprocess.run(command, capture_output=True, text=True, timeout=10)
    assert result.returncode and "Sandboxing will not be disabled" in result.stderr, result.stderr
    assert not list(runtime.iterdir()), "Failed sandbox preflight published IPC state"
    print("Missing engine and failed sandbox refuse startup before publishing IPC files.")
