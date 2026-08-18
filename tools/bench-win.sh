#!/usr/bin/env bash
# Package the viewer project, ship it to the Windows box, and benchmark there.
#
# The Mac can only give ratios. drjohnson has to be judged on the RTX 3060 under D3D12,
# because that is the machine whose fan is the complaint.
#
#   bash tools/bench-win.sh                       # all deployed scenes
#   bash tools/bench-win.sh playroom,drjohnson    # a subset
#   VDGS_BENCH_SORTNTH=10000 bash tools/bench-win.sh drjohnson
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST="${VDGS_HOST:-user@windows-box}"
SCENES="${1:-playroom,bonsai,drjohnson,drjohnson-shc}"
SIZE="${VDGS_BENCH_SIZE:-1024}"
FRAMES="${VDGS_BENCH_FRAMES:-120}"
SORTNTH="${VDGS_BENCH_SORTNTH:-1}"
# Framing the whole capture culls nothing; the drone flies inside it.
INSIDE="${VDGS_BENCH_INSIDE:-0}"
CULL="${VDGS_BENCH_CULL:-1}"
CULLMARGIN="${VDGS_BENCH_CULLMARGIN:-4}"

quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

# Library/ and Temp/ are rebuilt on the far side and dwarf everything else.
echo "== packaging viewer project =="
TAR="$(mktemp -t vdgs-bench).tgz"
tar -czf "$TAR" -C "$ROOT/unity/VDGSBundler" \
    --exclude='Library' --exclude='Temp' --exclude='Logs' --exclude='obj' \
    Assets Packages ProjectSettings
echo "   $(du -h "$TAR" | cut -f1)"

echo "== uploading =="
scp -o BatchMode=yes -q "$TAR" "$HOST:%USERPROFILE%/vdgs-bench.tgz" 2>&1 | quiet
scp -o BatchMode=yes -q "$ROOT/tools/bench-win.ps1" "$HOST:%USERPROFILE%/bench-win.ps1" 2>&1 | quiet
rm -f "$TAR"

echo "== benchmarking on $HOST =="
ssh -o BatchMode=yes "$HOST" \
  "powershell -ExecutionPolicy Bypass -File %USERPROFILE%\\bench-win.ps1 -Scenes $SCENES -Size $SIZE -Frames $FRAMES -SortNth $SORTNTH -Inside $INSIDE -Cull $CULL -CullMargin $CULLMARGIN" \
  2>&1 | quiet
