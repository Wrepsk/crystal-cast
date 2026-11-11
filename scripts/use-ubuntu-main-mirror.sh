#!/bin/sh
# GitHub's Ubuntu runners fetch packages through mirror+file:/etc/apt/apt-mirrors.txt, which starts with the
# Azure-hosted mirror (azure.archive.ubuntu.com). When that mirror is unreachable, apt keeps retrying it for
# a long time. This points apt straight at Ubuntu's own mirror and checks, before any download, that nothing
# still goes through the mirror list or the Azure host.
set -eu

main_mirror="http://archive.ubuntu.com/ubuntu/"

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
    -e "s#mirror\+file:/etc/apt/apt-mirrors\.txt/?#${main_mirror}#g" \
    -e "s#https?://azure\.archive\.ubuntu\.com/ubuntu/?#${main_mirror}#g" \
    "$file"
done

# Anything that still reads the mirror list gets only Ubuntu's main mirror.
if [ -f /etc/apt/apt-mirrors.txt ]; then
  printf '%s\n' "$main_mirror" | sudo tee /etc/apt/apt-mirrors.txt >/dev/null
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
printf '%s\n' "$uris" | sed -E 's#^([a-z]+://[^/]+).*#\1#' | sort -u

if printf '%s\n' "$uris" | grep -qE 'azure\.archive\.ubuntu\.com|mirror\+file'; then
  echo "::error::apt still uses the Azure mirror or the mirror list."
  exit 1
fi

if [ -z "$uris" ]; then
  echo "::error::apt reported no package sources."
  exit 1
fi
