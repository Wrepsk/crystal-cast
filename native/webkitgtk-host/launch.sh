#!/bin/sh
# Started by CrystalCast from inside Wine as: sh launch.sh <helper> [helper options]
# Files extracted by a Windows plugin installer are not executable, so mark the
# helper executable first. All output goes to the log the plugin shows on failure.
# The final exit line lets the plugin notice a failed start without waiting.
helper=$1
shift
log=${CRYSTALCAST_HOST_LOG:-/dev/null}
chmod u+x "$helper" >"$log" 2>&1
"$helper" "$@" >>"$log" 2>&1
echo "CRYSTALCAST_HOST_EXIT $?" >>"$log"
