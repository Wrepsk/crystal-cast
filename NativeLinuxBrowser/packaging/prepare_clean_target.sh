#!/bin/bash
# Development containers only. Deliberately installs no WPE/GStreamer packages.
set -euo pipefail
case "$1" in
    ubuntu|debian) apt-get update -qq; apt-get install -y --no-install-recommends python3 ca-certificates > /tmp/crystalcast-target-install.log 2>&1 ;;
    fedora) dnf --setopt=timeout=20 --setopt=retries=1 --disablerepo=fedora-cisco-openh264 install -y python3 ca-certificates > /tmp/crystalcast-target-install.log 2>&1 ;;
    arch) pacman -Syu --noconfirm python ca-certificates > /tmp/crystalcast-target-install.log 2>&1 ;;
    *) exit 2 ;;
esac
if ! id tester >/dev/null 2>&1; then useradd -m tester; fi
sleep infinity
