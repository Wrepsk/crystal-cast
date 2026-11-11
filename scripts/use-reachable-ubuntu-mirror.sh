#!/bin/sh
# GitHub's Ubuntu runners fetch packages through mirror+file:/etc/apt/apt-mirrors.txt, which starts with the
# Azure-hosted mirror (azure.archive.ubuntu.com). When Ubuntu's hosts can't be reached from the runner, apt
# keeps retrying them for a long time. This picks the first Ubuntu mirror that answers, points every Ubuntu
# source at it, and checks, before any download, that apt will fetch only from that mirror.
#
# Any mirror is safe to use: apt still checks every package against Ubuntu's signing key.
set -eu

candidates="
http://archive.ubuntu.com/ubuntu/
http://mirrors.edge.kernel.org/ubuntu/
http://mirrors.mit.edu/ubuntu/
"

codename=$(. /etc/os-release && printf '%s' "$VERSION_CODENAME")
mirror=""
for candidate in $candidates; do
  if curl -fsS --max-time 10 -o /dev/null "${candidate}dists/${codename}/InRelease"; then
    mirror="$candidate"
    break
  fi
  echo "Mirror not reachable: $candidate"
done

if [ -z "$mirror" ]; then
  echo "::error::No Ubuntu mirror is reachable from this runner."
  exit 1
fi
echo "Using Ubuntu mirror: $mirror"

source_files=""
for file in /etc/apt/sources.list /etc/apt/sources.list.d/*.sources /etc/apt/sources.list.d/*.list; do
  [ -f "$file" ] && source_files="$source_files $file"
done

if [ -z "$source_files" ]; then
  echo "::error::No apt source files found."
  exit 1
fi

for file in $source_files; do
  sudo sed -i -E \
    -e "s#mirror\+file:/etc/apt/apt-mirrors\.txt/?#${mirror}#g" \
    -e "s#https?://(azure\.archive|archive|security)\.ubuntu\.com/ubuntu/?#${mirror}#g" \
    "$file"
done

# Anything that still reads the mirror list gets only the chosen mirror.
if [ -f /etc/apt/apt-mirrors.txt ]; then
  printf '%s\n' "$mirror" | sudo tee /etc/apt/apt-mirrors.txt >/dev/null
fi

# Give up on a dead mirror quickly instead of waiting minutes per file.
printf '%s\n' \
  'Acquire::Retries "3";' \
  'Acquire::http::Timeout "20";' \
  'Acquire::https::Timeout "20";' \
  | sudo tee /etc/apt/apt.conf.d/80-ci-network >/dev/null

echo "apt sources now:"
# shellcheck disable=SC2086
grep -hE '^[[:space:]]*(URIs:|deb )' $source_files || true

# Verify the configuration apt will actually use: the index addresses it would fetch, without fetching them.
uris=$(sudo apt-get update --print-uris -q | cut -d"'" -f2)
echo "apt would fetch from:"
printf '%s\n' "$uris" | sed -E 's#^([a-z+]+:/*[^/]+).*#\1#' | sort -u

if [ -z "$uris" ]; then
  echo "::error::apt reported no package sources."
  exit 1
fi

# Every Ubuntu index must come from the chosen mirror; other repositories (such as Microsoft's) are left alone.
stray=$(printf '%s\n' "$uris" | grep -E 'mirror\+file|\.ubuntu\.com/ubuntu/|/ubuntu/dists/' | grep -vF "$mirror" || true)
if [ -n "$stray" ]; then
  echo "::error::apt would still fetch Ubuntu packages from another mirror:"
  printf '%s\n' "$stray" | head -5
  exit 1
fi
