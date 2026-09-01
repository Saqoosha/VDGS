#!/usr/bin/env bash
# Package the viewer project, ship it to the Windows box, and benchmark there.
#
# The Mac can only give ratios. A capture has to be judged on the GPU that actually
# flies the sim, under D3D12.
#
#   bash tools/bench-win.sh                       # all deployed scenes
#   bash tools/bench-win.sh playroom,drjohnson    # a subset
#   VDGS_BENCH_SORTNTH=10000 bash tools/bench-win.sh drjohnson
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
. "$ROOT/tools/_remote.sh"
SCENES="${1:-playroom,bonsai,drjohnson,drjohnson-shc}"
SIZE="${VDGS_BENCH_SIZE:-1024}"
FRAMES="${VDGS_BENCH_FRAMES:-120}"
SORTNTH="${VDGS_BENCH_SORTNTH:-1}"
# Framing the whole capture culls nothing; the drone flies inside it.
INSIDE="${VDGS_BENCH_INSIDE:-0}"
CULL="${VDGS_BENCH_CULL:-1}"
CULLMARGIN="${VDGS_BENCH_CULLMARGIN:-4}"

quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

REMOTE_GAME=""
if [ -n "${VDGS_GAME:-}" ]; then
  REMOTE_GAME="\$env:VDGS_GAME = '$(printf '%s' "$VDGS_GAME" | sed "s/'/''/g")'; "
fi

# Library/ and Temp/ are rebuilt on the far side and dwarf everything else.
echo "== packaging viewer project =="
TAR="$(mktemp -t vdgs-bench).tgz"
tar -czf "$TAR" -C "$ROOT/unity/VDGSBundler" \
    --exclude='Library' --exclude='Temp' --exclude='Logs' --exclude='obj' \
    Assets Packages ProjectSettings
echo "   $(du -h "$TAR" | cut -f1)"

remote_root_mkdir
echo "== uploading =="
scp -o BatchMode=yes -q "$TAR" "$HOST:$REMOTE_ROOT/vdgs-bench.tgz" 2>&1 | quiet
scp -o BatchMode=yes -q "$ROOT/tools/bench-win.ps1" "$HOST:$REMOTE_ROOT/bench-win.ps1" 2>&1 | quiet
rm -f "$TAR"

echo "== benchmarking on $HOST =="
ssh -o BatchMode=yes "$HOST" \
  "${REMOTE_GAME}powershell -ExecutionPolicy Bypass -File (Join-Path $REMOTE_ROOT_PS 'bench-win.ps1') -Scenes $SCENES -Size $SIZE -Frames $FRAMES -SortNth $SORTNTH -Inside $INSIDE -Cull $CULL -CullMargin $CULLMARGIN" \
  2>&1 | quiet
