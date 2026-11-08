#!/bin/sh
# Local runtime checks only; no package manager or Wine installer.
set -eu
if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then
    printf '%s\n' 'This package requires x86_64 Linux.' >&2
    exit 2
fi
if ! command -v python3 >/dev/null 2>&1 || ! python3 -c 'import sys; sys.exit(sys.version_info < (3,10))'; then
    printf '%s\n' 'Python 3.10 or newer is required. No development headers are needed.' >&2
    exit 2
fi
BENCH_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ -f "$BENCH_DIR/native/bin/gtk-frame-probe" ] && [ ! -x "$BENCH_DIR/native/bin/gtk-frame-probe" ]; then
    chmod u+x "$BENCH_DIR/native/bin/gtk-frame-probe" 2>/dev/null || true
fi
exec python3 "$BENCH_DIR/launch.py" "$@"
