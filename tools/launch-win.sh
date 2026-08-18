#!/usr/bin/env bash
# Ship the launch script to the Windows box and run it there.
#
# The script used to live only on the far side, hand-edited, with a stale copy in this
# repo that had drifted apart from it. Shipping it every time is what keeps one of them
# authoritative.
#
#   bash tools/launch-win.sh                        # launch and leave the game running
#   bash tools/launch-win.sh -Diagnose              # ...then dump logs and stop it
#   bash tools/launch-win.sh -GameArgs '-force-vulkan'
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST="${VDGS_HOST:-user@windows-box}"
quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

scp -o BatchMode=yes -q "$ROOT/tools/launch-win.ps1" "$HOST:%USERPROFILE%/launch-win.ps1" 2>&1 | quiet
ssh -o BatchMode=yes "$HOST" \
  "powershell -ExecutionPolicy Bypass -File %USERPROFILE%\\launch-win.ps1 $*" 2>&1 | quiet
