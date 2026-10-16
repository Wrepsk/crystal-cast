#!/bin/bash
# Clean-container test: install no WPE/GStreamer packages on the target.
set -euo pipefail
archive=$(realpath "$1")
tests=$(realpath "$2")
fixture=${3:-}
if [ -n "$fixture" ]; then fixture=$(realpath "$fixture"); fi
controller=${4:-/dev/null}
controller=$(realpath "$controller")
progressive=${5:-}
if [ -n "$progressive" ]; then progressive=$(realpath "$progressive"); fi
runtime=$(mktemp -d)
chmod 700 "$runtime"
trap 'rm -rf -- "$runtime"' EXIT
python3 - "$archive" "$runtime" <<'PY'
import pathlib, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    archive.extractall(sys.argv[2])
    for entry in archive.infolist():
        path = pathlib.Path(sys.argv[2]) / entry.filename
        path.chmod(0o755 if entry.external_attr >> 16 & 0o111 else 0o644)
PY
cd "$tests"
"$runtime/bin/crystalcast-browser" --diagnose
python3 smoke.py --helper "$runtime/bin/crystalcast-browser" --transport shared-file --seconds 5 --output /tmp/crystalcast-portable-smoke.json
cat /tmp/crystalcast-portable-smoke.json
if [ -n "$progressive" ]; then
    python3 video.py --helper "$runtime/bin/crystalcast-browser" --fixture "$progressive" --script "$controller" \
        --width 1920 --height 1080 --video-width 1920 --seconds 12 --transport shared-file \
        --output /tmp/crystalcast-portable-progressive.json
fi
if [ -n "$fixture" ]; then
    python3 video.py --helper "$runtime/bin/crystalcast-browser" --fixture "$fixture" --script "$controller" \
        --width 1920 --height 1080 --video-width 1920 --seconds 12 --mse --mse-codecs avc1.42c02a,mp4a.40.2 \
        --transport shared-file --output /tmp/crystalcast-portable-mse.json
fi
