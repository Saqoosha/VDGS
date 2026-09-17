#!/usr/bin/env bash
# Fly a circle around a point in the real game and bring the frames back.
#
# One launch, one screenshot per pose, nobody watching. The camera is pinned through
# <game>/vdgs/evalcam.json, which the plugin re-reads once a second, so the path is exact
# and repeatable - which is what makes two runs comparable.
#
#   bash tools/evalorbit.sh out/            # 12 poses around the Himeji capture
#   VDGS_ORBIT_SPLAT=7a7bfaea VDGS_ORBIT_CENTRE=0,40,0 VDGS_ORBIT_RADIUS=250 \
#   VDGS_ORBIT_HEIGHT=90 VDGS_ORBIT_STEPS=12 bash tools/evalorbit.sh out/
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
. "$ROOT/tools/_remote.sh"
quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

OUT="${1:?usage: evalorbit.sh <out dir>}"
SPLAT="${VDGS_ORBIT_SPLAT:-7a7bfaea}"
SPLAT2="${VDGS_ORBIT_SPLAT2:-}"
CENTRE="${VDGS_ORBIT_CENTRE:-0,40,0}"
RADIUS="${VDGS_ORBIT_RADIUS:-250}"
HEIGHT="${VDGS_ORBIT_HEIGHT:-90}"
STEPS="${VDGS_ORBIT_STEPS:-12}"
FOV="${VDGS_ORBIT_FOV:-70}"

mkdir -p "$OUT"
POSES="$(mktemp -t vdgs-poses)"
python3 - "$POSES" "$CENTRE" "$RADIUS" "$HEIGHT" "$STEPS" "$FOV" <<'PY'
import json, math, sys
out, centre, radius, height, steps, fov = sys.argv[1:]
cx, cy, cz = (float(v) for v in centre.split(','))
radius, height, steps, fov = float(radius), float(height), int(steps), float(fov)
poses = []
for i in range(steps):
    t = i / steps * 2 * math.pi
    pos = [cx + math.sin(t) * radius, height, cz + math.cos(t) * radius]
    d = [cx - pos[0], cy - pos[1], cz - pos[2]]
    n = math.sqrt(sum(v * v for v in d))
    poses.append({"pos": [round(v, 3) for v in pos],
                  "fwd": [round(v / n, 5) for v in d],
                  "up": [0, 1, 0], "fov": fov})
json.dump(poses, open(out, 'w'))
print(f'{steps} poses, radius {radius:g} m at height {height:g} m around {cx:g},{cy:g},{cz:g}')
PY

remote_root_mkdir
ssh -o BatchMode=yes "$HOST" \
  "New-Item -ItemType Directory -Force -Path (Join-Path $REMOTE_ROOT_PS 'vdgs-stage') | Out-Null" >/dev/null 2>&1
scp -o BatchMode=yes -q "$POSES" "$HOST:$REMOTE_ROOT/vdgs-stage/poses.json" 2>&1 | quiet
scp -o BatchMode=yes -q "$ROOT/tools/evalorbit-win.ps1" "$HOST:$REMOTE_ROOT/evalorbit-win.ps1" 2>&1 | quiet
rm -f "$POSES"

REMOTE_GAME=""
if [ -n "${VDGS_GAME:-}" ]; then
  REMOTE_GAME="\$env:VDGS_GAME = '$(printf '%s' "$VDGS_GAME" | sed "s/'/''/g")'; "
fi

echo "== flying =="
ssh -o BatchMode=yes "$HOST" \
  "${REMOTE_GAME}powershell -ExecutionPolicy Bypass -File (Join-Path $REMOTE_ROOT_PS 'evalorbit-win.ps1') -Splat '$SPLAT' -Splat2 '$SPLAT2' -Poses (Join-Path (Join-Path $REMOTE_ROOT_PS 'vdgs-stage') 'poses.json')" \
  2>&1 | quiet

scp -o BatchMode=yes -q "$HOST:$REMOTE_ROOT/orbit/*.png" "$OUT/" 2>&1 | quiet
echo "-> $OUT ($(ls "$OUT"/shot*.png 2>/dev/null | wc -l | tr -d ' ') frames)"
