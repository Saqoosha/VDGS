#!/usr/bin/env bash
# Show a capture and the collision shell generated from it, side by side, in a browser.
#
# This exists because the collision mesh has to be judged before it goes near the game:
# whether the room is enclosed, whether the floor is really there, whether the voxel
# size swallowed a doorway. Flying VelociDrone to find that out costs a deploy, a
# shader bake and a trip to the Windows box.
#
#   bash tools/preview.sh playroom                 # scene from the SCENES table
#   bash tools/preview.sh playroom 0.04            # indoor default; coarser = fatter
#   bash tools/preview.sh path/to/any.ply
#
# Collision is OpenVDB (docs/SCENES.md). splat-transform's voxel mesher is not used:
# it was measured at ~8x the gap of the level-set path. If vdb_tool is not on PATH,
# this script tries WSL on the Windows box (VDGS_HOST / tools/local.env). If that is
# not set either, it fails — it does not fall back to the old mesher.
#
# FRAMES: mirrored once at the top if the capture needs it, then nothing else is
# transformed anywhere. Both the collision mesh and the .sog come off the same ply, so
# they share a frame by construction.
#
# Whether a capture needs the mirror is PER SCENE and is measured, not assumed. See
# scene_mirror() below and `python3 tools/updir.py <ply>`. reprocess.sh keeps its own
# copy; SplatCollision.Attach mirrors every .ply. Three tables is the defect; this
# comment is the symptom.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ST="npx -y @playcanvas/splat-transform@3.3.0"
PORT="${VDGS_PREVIEW_PORT:-8790}"
VOXEL="${2:-0.04}"

scene_source() {
  case "$1" in
    bonsai)    echo bonsai2-aligned.ply ;;
    playroom)  echo playroom-nocrop.ply ;;
    drjohnson) echo drjohnson-aligned.ply ;;
    luigi)     echo luigi.ply ;;
    calico)    echo calico-lod3.ply ;;
    textilni)  echo textilni-lod3.ply ;;
    *)         echo "" ;;
  esac
}

# Measured 2026-08-19 via updir.py. luigi is too close to call (57.5 / 42.5) and is
# left alone here; reprocess.sh mirrors it to match the asset that had already shipped.
scene_mirror() {
  case "$1" in
    bonsai|drjohnson|calico) echo yes ;;
    *)                       echo no ;;
  esac
}

need_vdb_tool() {
  if command -v vdb_tool >/dev/null 2>&1; then
    echo local
    return
  fi
  if [ -n "${VDGS_HOST:-}" ] || [ -f "$ROOT/tools/local.env" ]; then
    echo wsl
    return
  fi
  echo missing
}

# Bake points.ply -> reduced.ply on Linux/WSL where vdb_tool lives.
bake_openvdb() {
  local points="$1" voxel="$2" fine="$3" reduced="$4"
  local where
  where="$(need_vdb_tool)"
  case "$where" in
    local)
      echo "-- OpenVDB (local vdb_tool)  voxel $voxel"
      vdb_tool -read "$points" \
        -points2ls voxel="$voxel" radius=2.0 width=4 \
        -median iter=1 -open radius=1 \
        -ls2mesh adapt=0.9 -write "$fine"
      python3 "$ROOT/tools/decimate_mesh.py" "$fine" "$reduced" 500000
      ;;
    wsl)
      echo "-- OpenVDB (WSL on \$VDGS_HOST)  voxel $voxel"
      # shellcheck disable=SC1091
      . "$ROOT/tools/_remote.sh"
      ssh -o BatchMode=yes -o ConnectTimeout=15 "$HOST" \
        "New-Item -ItemType Directory -Force -Path (Join-Path \$env:USERPROFILE 'vdgs-stage') | Out-Null" \
        >/dev/null
      scp -o BatchMode=yes -q "$points" "$HOST:vdgs-stage/preview-points.ply"
      scp -o BatchMode=yes -q "$ROOT/tools/decimate_mesh.py" "$HOST:vdgs-stage/decimate_mesh.py"
      local remote
      remote="$(mktemp -t vdgs-preview-vdb)"
      cat >"$remote" <<EOF
set -euo pipefail
WIN=\$(powershell.exe -NoProfile -Command 'Write-Output \$env:USERPROFILE' 2>/dev/null | tr -d '\r')
STAGE=\$(wslpath "\$WIN/vdgs-stage")
IN="\$STAGE/preview-points.ply"
FINE="\$STAGE/preview-fine.ply"
OUT="\$STAGE/preview-reduced.ply"
PY=python3
if [ -x "\$HOME/vdgsvenv/bin/python" ]; then PY="\$HOME/vdgsvenv/bin/python"; fi
command -v vdb_tool >/dev/null 2>&1 || { echo "vdb_tool missing inside WSL. apt install libopenvdb-tools" >&2; exit 1; }
vdb_tool -read "\$IN" \
  -points2ls voxel=$voxel radius=2.0 width=4 \
  -median iter=1 -open radius=1 \
  -ls2mesh adapt=0.9 -write "\$FINE"
"\$PY" "\$STAGE/decimate_mesh.py" "\$FINE" "\$OUT" 500000
EOF
      bash "$ROOT/tools/wsl.sh" "$remote"
      rm -f "$remote"
      scp -o BatchMode=yes -q "$HOST:vdgs-stage/preview-reduced.ply" "$reduced"
      ;;
    *)
      echo "preview.sh: vdb_tool is not on PATH, and VDGS_HOST is not set." >&2
      echo "Collision preview uses OpenVDB only (the splat-transform voxel path is 8x worse)." >&2
      echo "Install libopenvdb-tools on Linux/WSL, or set VDGS_HOST (see docs/SCENES.md)." >&2
      exit 1
      ;;
  esac
}

arg="${1:?usage: bash tools/preview.sh <scene|path.ply> [voxelSize]}"
if [ -f "$arg" ]; then
  SRC="$arg"; NAME="$(basename "${arg%.ply}")"
else
  NAME="$arg"
  src_name="$(scene_source "$NAME")"
  [ -n "$src_name" ] || { echo "unknown scene '$NAME' - pass a .ply path instead" >&2; exit 1; }
  SRC="$ROOT/build/testdata/scenes/$src_name"
fi
[ -f "$SRC" ] || { echo "missing $SRC" >&2; exit 1; }

OUT="$ROOT/build/preview/$NAME"
mkdir -p "$OUT"

echo "== $NAME  <-  $(basename "$SRC")   voxel $VOXEL"

if [ "$(scene_mirror "$NAME")" = "yes" ]; then
  echo "-- mirror y (tools/updir.py says this capture is floor-up)"
  python3 "$ROOT/tools/align_ply.py" "$SRC" "$OUT/.src.ply" --mirror y >/dev/null
  SRC="$OUT/.src.ply"
fi

echo "-- collision"
python3 "$ROOT/tools/align_ply.py" "$SRC" "$OUT/.big.ply" --max-sigma 5 \
    | grep -E "removed|drawn|largest" || true
$ST -w "$OUT/.big.ply" --filter-floaters --filter-cluster "$OUT/.clean.ply" 2>&1 \
    | grep -E "removed|cluster is|Error" || true
python3 "$ROOT/tools/ply_points.py" "$OUT/.clean.ply" "$OUT/.points.ply"
bake_openvdb "$OUT/.points.ply" "$VOXEL" "$OUT/.fine.ply" "$OUT/.reduced.ply"
python3 "$ROOT/tools/clean_mesh.py" "$OUT/.reduced.ply" "$OUT/.mesh.ply" \
    --voxel "$VOXEL" --min-voxels 100 --min-extent 0.25
python3 "$ROOT/tools/mesh_to_glb.py" "$OUT/.mesh.ply" "$OUT/collision.glb"

echo "-- splat"
$ST -w "$SRC" "$OUT/viewer.sog" 2>&1 | grep -E "Error|\.sog" || true

python3 "$ROOT/tools/preview_meta.py" "$NAME" "$VOXEL" \
        "$OUT/.clean.ply" "$OUT/collision.glb" "$OUT/preview.json"
rm -f "$OUT/.big.ply" "$OUT/.clean.ply" "$OUT/.src.ply" "$OUT/.points.ply" \
      "$OUT/.fine.ply" "$OUT/.reduced.ply" "$OUT/.mesh.ply"

cp "$ROOT/tools/preview/index.html" "$OUT/index.html"
ENGINE="$ROOT/build/preview/_engine"
mkdir -p "$OUT/_engine"
cp "$ENGINE/node_modules/playcanvas/build/playcanvas.mjs" "$OUT/_engine/"
cp "$ENGINE/node_modules/playcanvas/scripts/esm/camera-controls.mjs" "$OUT/_engine/"

echo "-- serving http://localhost:$PORT/$NAME/"
cd "$ROOT/build/preview"
python3 -m http.server --bind 127.0.0.1 "$PORT" >/dev/null 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
sleep 1
open "http://localhost:$PORT/$NAME/"
echo "-- ctrl-c to stop"
wait $SERVER
