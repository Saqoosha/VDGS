#!/usr/bin/env bash
# Show a capture and the collision shell generated from it, side by side, in a browser.
#
# This exists because the collision mesh has to be judged before it goes near the game:
# whether the room is enclosed, whether the floor is really there, whether the voxel
# size swallowed a doorway. Flying VelociDrone to find that out costs a deploy, a
# shader bake and a trip to the Windows box.
#
#   bash tools/preview.sh playroom                 # scene from the SCENES table
#   bash tools/preview.sh playroom 0.24            # try a coarser voxel
#   bash tools/preview.sh path/to/any.ply
#
# FRAMES: mirrored once at the top if the capture needs it, then nothing else is
# transformed anywhere. Both the collision mesh and the .sog come off the same ply, so
# they share a frame by construction. PlayCanvas renders gsplat data untransformed
# (verified by reading the loaded resource's centres back out of the page - they matched
# the source ply exactly, and the engine's gsplat shader chunks contain no negation).
#
# An earlier version mirrored the splat and negated the mesh as two separate steps. Being
# wrong in the same direction, they agreed with each other - matching bounding boxes,
# small gap, coverage ok - while the room rendered upside down. Nothing in here can catch
# that, because every check compares the collision to the capture. Saqoosha caught it from
# the rocket stickers on playroom's wall.
#
# Whether a capture needs the mirror is PER SCENE and is measured, not assumed. See
# scene_mirror() below and `python3 tools/updir.py <ply>`.
#
# reprocess.sh makes the same per-scene decision, so the converted assets in build/splats
# and this preview agree - VERIFIED FOR playroom (regenerated: source frame, bounds
# byte-identical to what was shipped) AND FOR NOTHING ELSE.
#
# The two tables are hand-copied and they already disagree. luigi is `no` here and `yes` in
# reprocess.sh, because updir.py cannot separate the two directions for it (57.5 / 42.5) and
# reprocess.sh chose to match the asset that had already shipped. So luigi previews in the
# opposite orientation to the one the game gets. calico is `yes` here and absent from
# reprocess.sh's SCENES, which is harmless only until somebody adds it.
#
# There is a third copy of this decision, in SplatCollision.Attach - it mirrors for any
# .ply scene. Three hand-maintained copies of one table is the actual defect here; this
# comment is the symptom. Give updir.py a --table mode and have all three read it.
#
# NOTE: this script still drives the old splat-transform voxel path. The chosen method is
# OpenVDB (see docs/superpowers/specs/2026-08-18-splat-collision-design.md); the pipeline
# has not been moved over yet.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ST="npx -y @playcanvas/splat-transform@3.3.0"
PORT="${VDGS_PREVIEW_PORT:-8790}"
VOXEL="${2:-0.12}"

# Same source table as reprocess.sh: a preview built from a different ply than the one
# that gets converted would be checking the wrong thing.
# A case rather than an associative array, because /bin/bash on macOS is still 3.2.
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

# Whether the capture needs Y mirrored to stand up, PER SCENE. reprocess.sh keeps its own
# copy of this decision and the two already differ - see the header.
# `python3 tools/updir.py <ply>` is where both come from; measured 2026-08-19:
#
#   playroom-nocrop    as-is  2.5%  mirrored 97.5%   -> no
#   drjohnson-aligned  as-is 97.5%  mirrored  2.5%   -> yes
#   bonsai2-aligned    as-is 97.5%  mirrored  2.5%   -> yes
#   calico-lod3        as-is 97.5%  mirrored  2.5%   -> yes
#   textilni-lod3      42.5 / 57.5, luigi 57.5 / 42.5  -> too close to call, left alone
scene_mirror() {
  case "$1" in
    bonsai|drjohnson|calico) echo yes ;;
    *)                       echo no ;;
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

# Mirror ONCE, up front, and feed the result to both the collision path and the .sog. Both
# then live in one frame by construction. The previous arrangement mirrored the splat and
# negated the mesh separately, which is how they ended up agreeing with each other while
# the room was upside down.
if [ "$(scene_mirror "$NAME")" = "yes" ]; then
  echo "-- mirror y (tools/updir.py says this capture is floor-up)"
  python3 "$ROOT/tools/align_ply.py" "$SRC" "$OUT/.src.ply" --mirror y >/dev/null
  SRC="$OUT/.src.ply"
fi

# --- collision: source ply, pruned twice, then voxelised ---------------------------
# Two different kinds of junk set the scene bounds, and each needs its own filter.
#
# --max-sigma drops gaussians that are enormous. utlida's largest is 180% of the whole
# scene extent and sits outside the capture; a connectivity filter cannot touch those,
# because something that wide overlaps everything and is connected by construction.
# --filter-cluster then drops what is merely far away.
#
# Both matter because voxel count goes with the cube of the grid edge: utlida's bounds
# were 414x the volume of its actual geometry, which put it past every limit the tool
# has, and coarsening the voxel did not help - the giants set the bounds either way, so
# a bigger voxel only changed which limit was hit first.
echo "-- collision"
python3 "$ROOT/tools/align_ply.py" "$SRC" "$OUT/.big.ply" --max-sigma 5 \
    | grep -E "removed|drawn|largest" || true
$ST -w "$OUT/.big.ply" --filter-floaters --filter-cluster "$OUT/.clean.ply" 2>&1 \
    | grep -E "removed|cluster is|Error" || true
# splat-transform names its outputs after the stem of the requested path, so asking for
# gen.voxel.json is what produces gen.collision.glb next to it.
$ST -w "$OUT/.clean.ply" --voxel-size "$VOXEL" --voxel-external-fill \
    --collision-mesh faces "$OUT/gen.voxel.json" 2>&1 | grep -E "triangles|Error" || true
# The grid's own bounding box is part of that mesh and is the one surface a drone can
# never reach. It also hides the whole scene from outside, so it goes.
python3 "$ROOT/tools/trim_collision.py" "$OUT/gen.collision.glb" "$OUT/collision.glb"

# --- splat: the same ply the collision came from -------------------------------------
# Any mirroring already happened at the top, so there is nothing to do here but convert.
echo "-- splat"
$ST -w "$SRC" "$OUT/viewer.sog" 2>&1 | grep -E "Error|\.sog" || true

# --- numbers ----------------------------------------------------------------------
# Measured against the cleaned ply, not the raw source: the shell was built from the
# cleaned one, so comparing it to anything else would report a gap that is not there.
python3 "$ROOT/tools/preview_meta.py" "$NAME" "$VOXEL" \
        "$OUT/.clean.ply" "$OUT/collision.glb" "$OUT/preview.json"
rm -f "$OUT/.big.ply" "$OUT/.clean.ply" "$OUT/.src.ply" \
      "$OUT/gen.voxel.json" "$OUT/gen.voxel.bin" "$OUT/gen.collision.glb"

cp "$ROOT/tools/preview/index.html" "$OUT/index.html"
ENGINE="$ROOT/build/preview/_engine"
mkdir -p "$OUT/_engine"
cp "$ENGINE/node_modules/playcanvas/build/playcanvas.mjs" "$OUT/_engine/"
cp "$ENGINE/node_modules/playcanvas/scripts/esm/camera-controls.mjs" "$OUT/_engine/"

echo "-- serving http://localhost:$PORT/$NAME/"
cd "$ROOT/build/preview"
# Loopback only: this serves build/preview, which holds whole captures.
python3 -m http.server --bind 127.0.0.1 "$PORT" >/dev/null 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
sleep 1
open "http://localhost:$PORT/$NAME/"
echo "-- ctrl-c to stop"
wait $SERVER
