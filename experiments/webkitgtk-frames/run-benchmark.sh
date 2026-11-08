#!/bin/sh
# Runtime checks only: never invokes a package manager, compiler, or sudo.
set -eu
if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then
    printf '%s\n' 'This test package requires x86_64 Linux.' >&2
    exit 2
fi
if ! command -v python3 >/dev/null 2>&1; then
    printf '%s\n' 'Python 3.10 or newer is missing. Install your distribution’s Python 3 runtime, then try again. No development headers are needed.' >&2
    exit 2
fi
if ! python3 -c 'import sys; sys.exit(sys.version_info < (3,10))'; then
    printf '%s\n' 'Python 3.10 or newer is required.' >&2
    exit 2
fi
BENCH_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ -f "$BENCH_DIR/bin/gtk-frame-probe" ] && [ ! -x "$BENCH_DIR/bin/gtk-frame-probe" ]; then
    # Some ZIP extractors discard Unix modes. Restore only this package's binary.
    chmod u+x "$BENCH_DIR/bin/gtk-frame-probe" 2>/dev/null || true
fi
exec python3 "$BENCH_DIR/run_benchmark.py" "$@"
