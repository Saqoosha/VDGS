#!/usr/bin/env bash
# Render one frame of the pinned evaluation camera, in the real game, with nobody watching.
#
# Writes <game>/vdgs/evalcam.json, launches the game, clicks QUICK START, waits for the
# track, screenshots, quits, and brings the PNG back. The camera pose is the same one a
# web viewer and tools/…/RenderCompare can be given, which is what makes the three
# images subtractable.
#
#   bash tools/evalshot.sh out.png                       # reuse the installed evalcam.json
#   bash tools/evalshot.sh out.png cam.json              # install this pose first
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
. "$ROOT/tools/_remote.sh"
quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

OUT="${1:?usage: evalshot.sh <out.png> [camera.json]}"
CAM="${2:-}"

if [ -n "$CAM" ]; then
  # Staging directory first: the game path contains spaces and PowerShell does not treat
  # a backslash as an escape, so scp straight to it silently loses the file.
  scp -o BatchMode=yes -q "$CAM" "$HOST:vdgs-stage/evalcam.json" 2>&1 | quiet
  ssh -o BatchMode=yes "$HOST" \
    'Copy-Item C:\Users\a\vdgs-stage\evalcam.json (Join-Path $env:USERPROFILE "Downloads\Velocidrone Windows Launcher\app\vdgs\evalcam.json") -Force' 2>&1 | quiet
fi

scp -o BatchMode=yes -q "$ROOT/tools/evalshot-win.ps1" "$HOST:vdgs-stage/evalshot-win.ps1" 2>&1 | quiet
ssh -o BatchMode=yes "$HOST" \
  'Copy-Item C:\Users\a\vdgs-stage\evalshot-win.ps1 C:\Users\a\evalshot-win.ps1 -Force; powershell -ExecutionPolicy Bypass -File C:\Users\a\evalshot-win.ps1' 2>&1 | quiet

scp -o BatchMode=yes -q "$HOST:vdgs-shot.png" "$OUT" 2>&1 | quiet
echo "-> $OUT"
