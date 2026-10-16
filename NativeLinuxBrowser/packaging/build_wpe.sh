#!/bin/bash
# Developer/CI build only. End users download the resulting precompiled runtime.
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
work_root=${CRYSTALCAST_BUILD_ROOT:-/var/tmp/crystalcast-runtime-build}
wpe_version=2.54.1
wpe_hash=0de8bdfba011f9cd63ba9a9171fa6038dd2c08237894f4082abbb821f29d3fe9
mkdir -p "$work_root"
archive="$work_root/wpewebkit-$wpe_version.tar.xz"
if [ ! -f "$archive" ]; then
    curl --fail --location --retry 3 "https://wpewebkit.org/releases/wpewebkit-$wpe_version.tar.xz" -o "$archive"
fi
printf '%s  %s\n' "$wpe_hash" "$archive" | sha256sum --check --status
source_root="$work_root/wpewebkit-$wpe_version"
if [ ! -f "$source_root/.crystalcast-paths-patched" ]; then
    tar -xf "$archive" -C "$work_root"
    python3 "$repo_root/packaging/patch_wpe_paths.py" "$source_root"
    touch "$source_root/.crystalcast-paths-patched"
fi
cmake -S "$source_root" -B "$work_root/wpe-build" -G Ninja \
    -DPORT=WPE -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_C_COMPILER=clang-18 -DCMAKE_CXX_COMPILER=clang++-18 \
    -DCMAKE_C_FLAGS='-march=x86-64 -mtune=generic' \
    -DCMAKE_CXX_FLAGS='-march=x86-64 -mtune=generic' \
    -DCMAKE_EXE_LINKER_FLAGS=-fuse-ld=lld -DCMAKE_SHARED_LINKER_FLAGS=-fuse-ld=lld \
    -DCMAKE_INSTALL_PREFIX=/crystalcast-linux-browser -DCMAKE_INSTALL_LIBDIR=lib \
    -DENABLE_DOCUMENTATION=OFF -DENABLE_INTROSPECTION=OFF \
    -DENABLE_SPELLCHECK=OFF -DENABLE_SPEECH_SYNTHESIS=OFF -DENABLE_WEBDRIVER=OFF \
    -DENABLE_WPE_LEGACY_API=OFF -DENABLE_WPE_PLATFORM=ON \
    -DENABLE_WPE_PLATFORM_HEADLESS=ON -DENABLE_WPE_PLATFORM_WAYLAND=ON -DENABLE_WPE_PLATFORM_DRM=ON \
    -DENABLE_BUBBLEWRAP_SANDBOX=ON -DUSE_SYSTEM_SYSPROF_CAPTURE=OFF -DUSE_LIBBACKTRACE=OFF
cmake --build "$work_root/wpe-build" --parallel "${CRYSTALCAST_BUILD_JOBS:-3}"
DESTDIR="$work_root/stage" cmake --install "$work_root/wpe-build" --strip
