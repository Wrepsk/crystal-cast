#!/usr/bin/env bash
# Developer/CI only. Tests a precompiled package in a disposable runtime-only OS.
set -euo pipefail
archive=$(realpath "${1:?Pass the benchmark tar.gz}")
engine=${CONTAINER_ENGINE:-docker}
output=$(realpath -m "${SMOKE_REPORT_DIR:-artifacts/package-smoke-report}")
mkdir -p "$output"
options=(-i --rm --security-opt seccomp=unconfined)
if [[ "$engine" == docker ]]; then options+=(--security-opt "apparmor=${BENCH_CONTAINER_APPARMOR:-unconfined}"); fi
"$engine" run "${options[@]}" -v "$archive:/input/test.tar.gz:ro" -v "$output:/reports" ubuntu:24.04 bash -s <<'RUNTIME'
set -euo pipefail
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends python3 libwebkitgtk-6.0-4 libepoxy0 gstreamer1.0-libav gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-bad gstreamer1.0-gl xvfb xauth dbus-x11 fonts-dejavu-core >/tmp/runtime-install.log 2>&1
for tool in cc gcc pkg-config ffmpeg; do
    if command -v "$tool" >/dev/null; then
        printf 'Unexpected developer tool in runtime-only test: %s\n' "$tool" >&2
        exit 1
    fi
done
install -d -m 700 -o 1000 -g 1000 /tmp/benchmark-runtime /home/ubuntu/test
tar -xzf /input/test.tar.gz --no-same-owner -C /home/ubuntu/test
chown 1000:1000 /reports
launcher=$(find /home/ubuntu/test -mindepth 2 -maxdepth 2 -name run-benchmark.sh -print -quit)
runuser -u ubuntu -- bwrap --ro-bind / / --dev /dev --proc /proc --unshare-user --unshare-pid --unshare-net true
runuser -u ubuntu -- env XDG_RUNTIME_DIR=/tmp/benchmark-runtime GTK_A11Y=none GDK_BACKEND=x11 GSK_RENDERER=cairo LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=4 WEBKIT_SKIA_ENABLE_CPU_RENDERING=1 WEBKIT_GST_DISABLE_GL_SINK=1 dbus-run-session -- xvfb-run -a -s '-screen 0 1920x1080x24' sh "$launcher" --seconds 3 --warmup 1 --diagnostics --output /reports/success
python3 - <<'PY'
import json
from pathlib import Path
r = json.loads(Path('/reports/success/results.json').read_text())
assert r['tests']['shm'].get('fully_independently_verified'), r
assert r['tests']['render-shm'].get('fully_independently_verified'), r
assert r['tests']['overlay-check']['integrity_passed'], r
assert r['tests']['direct']['status'] == 'unavailable', r
assert all(not x.get('sustained_1080p60_verified') for x in r['tests'].values())
for path in Path('/reports/success').iterdir():
    text = path.read_text()
    assert not any(x in text for x in ('/home/', '/root/', '127.0.0.1', 'localhost', '/tmp/', 'machine-id'))
print('Extracted package: actual H.264 frames, independent integrity, overlay, unavailable direct route and privacy checks passed.')
PY
if runuser -u ubuntu -- env -u DISPLAY -u WAYLAND_DISPLAY sh "$launcher" --check --output /reports/no-desktop; then
    printf '%s\n' 'Expected a clean unavailable result without a desktop.' >&2
    exit 1
fi
python3 - <<'PY'
import json
from pathlib import Path
r = json.loads(Path('/reports/no-desktop/results.json').read_text())
assert any('desktop' in x for x in r['dependency_problems'])
assert r['tests'] == {}
PY
RUNTIME
test -s "$output/success/results.json"
test -s "$output/no-desktop/results.json"
