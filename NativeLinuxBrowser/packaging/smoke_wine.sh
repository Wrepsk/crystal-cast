#!/bin/bash
# Developer test only: isolated disposable Wine prefix, never a game prefix.
set -euo pipefail
archive=$(realpath "$1")
probe=$(realpath "$2")
wine=${WINE64:-/usr/lib/wine/wine64}
wineserver=${WINESERVER:-/usr/lib/wine/wineserver64}
work=$(mktemp -d)
export WINEPREFIX="$work/prefix" XDG_RUNTIME_DIR="$work/session" WINEDEBUG=-all
mkdir -m 700 "$XDG_RUNTIME_DIR" "$work/runtime with spaces"
cleanup() {
    # Scope every server command to this test's unique prefix.
    "$wineserver" -k >/dev/null 2>&1 || true
    timeout 10 "$wineserver" -w >/dev/null 2>&1 || true
    rm -rf -- "$work"
}
trap cleanup EXIT
python3 - "$archive" "$work/runtime with spaces" <<'PY'
from pathlib import Path
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    archive.extractall(sys.argv[2])
    for item in archive.infolist():
        (Path(sys.argv[2]) / item.filename).chmod(0o755 if item.external_attr >> 16 & 0o111 else 0o644)
    # Exercise the installer's native chmod/completion bridge: the harness must
    # restore this bit before it can execute the bootstrap.
    (Path(sys.argv[2]) / 'bin/crystalcast-browser').chmod(0o644)
PY
"$wine" "$probe" "$work/runtime with spaces/bin/crystalcast-browser"
