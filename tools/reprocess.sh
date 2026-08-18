#!/usr/bin/env bash
# Re-derive every splat scene from its aligned source.
#
# One Y reflection is the whole transform. 3DGS ply is right-handed Y-down and
# Unity is left-handed Y-up, and UnityGaussianSplatting converts neither, so a
# capture read straight in comes out mirrored *and* upside down. Reflecting Y
# fixes both at once - it is a reflection (determinant -1), which a rotation can
# never be, which is why the earlier --rotate 180,0,0 left everything mirrored.
#
# No cropping. Percentile cropping trims the outer shell, and a room shot from
# the inside has its walls there, so it visibly thins the scene.
#
#   bash tools/reprocess.sh              # all scenes
#   bash tools/reprocess.sh playroom     # just one
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${VDGS_UNITY:-/Applications/Unity/Hub/Editor/2022.3.42f1/Unity.app/Contents/MacOS/Unity}"
CONVERTER="$ROOT/unity/VDGSConverter"
QUALITY="${VDGS_QUALITY:-High}"
# High is Norm16 positions and scales, Float16x4 colour, Norm11 spherical harmonics -
# 84 bytes per splat, and it beats every other tier measured on the RTX 3060:
#
#   drjohnson  Float32 everything    236 B/splat   14.01 ms   reference
#              Cluster16k SH          47 B/splat    9.38 ms   1.44/255 mean difference
#              High                   84 B/splat    8.80 ms   0.09/255
#
# Palette-compressed SH is smaller but slower: each splat reads a two-byte index and then
# scatters into a 3 MB palette, and that indirection costs more than reading Norm11 SH in
# sequence. High is also the most faithful of the three, and needs no k-means, so a
# runtime loader can produce it.
#
# Do NOT drop to Medium. Norm11/Norm8x4/Norm6 renders drjohnson 2.6x too dark
# (58.83/255 mean difference) - whether that is upstream's tier or our port is unresolved.
SH_FORMAT="${VDGS_SH_FORMAT:-}"   # empty = whatever the quality preset picks

# scene:source  — sources are SuperSplat exports unless noted
SCENES=(
  "bonsai:bonsai2-aligned.ply"
  # NOT playroom-aligned.ply: that is the raw export, three times larger in every
  # dimension. The scene actually in use came from playroom-nocrop.ply, already
  # room-scaled with its floor near y=0.
  "playroom:playroom-nocrop.ply"
  "drjohnson:drjohnson-aligned.ply"
  "luigi:luigi.ply"
)

want="${1:-}"

for entry in "${SCENES[@]}"; do
  name="${entry%%:*}"
  src="$ROOT/build/testdata/${entry#*:}"

  [ -n "$want" ] && [ "$want" != "$name" ] && continue
  if [ ! -f "$src" ]; then
    echo "!! $name: missing $src" >&2
    continue
  fi

  echo "=============================================================="
  echo "== $name  <-  $(basename "$src")"
  echo "=============================================================="

  mirrored="$ROOT/build/testdata/.$name-mirrored.ply"
  python3 "$ROOT/tools/align_ply.py" "$src" "$mirrored" --mirror y --rotate 0,0,0 \
    | grep -E "^input|mirrored|density check|WARNING|bounds" || true

  # No `|| true` here, and no swallowing the exit status through a pipe. Unity refuses to
  # open a project a previous instance still holds, and a compile error in the exporter
  # exits non-zero - both used to look like success and leave the previous conversion in
  # place, which is how a scene silently stayed stale.
  log="$(mktemp -t vdgs-convert)"
  set +e
  ( cd "$CONVERTER" && "$UNITY" -batchmode -quit -nographics \
      -projectPath "$CONVERTER" \
      -executeMethod PlyExporter.Run \
      -vdgsInput "$mirrored" \
      -vdgsOutput "$ROOT/build/splats/$name" \
      -vdgsQuality "$QUALITY" \
      ${SH_FORMAT:+-vdgsShFormat "$SH_FORMAT"} \
      -logFile - ) >"$log" 2>&1
  status=$?
  set -e
  grep -E "\[VDGS\] (export|SH format)|fatal|error CS" "$log" | head -6 || true
  if [ $status -ne 0 ] || ! grep -q "\[VDGS\] exported" "$log"; then
    echo "!! $name: conversion FAILED (exit $status) - full log at $log" >&2
    rm -f "$mirrored"
    exit 1
  fi
  rm -f "$log"

  # The data is already correct in world terms; placement stays identity so the
  # in-game scale/height controls start from a known baseline.
  cat > "$ROOT/build/splats/$name/placement.json" <<'EOF'
{
    "position": [0.0, 0.0, 0.0],
    "rotation": [0.0, 0.0, 0.0],
    "scale": 1.0
}
EOF
  rm -f "$mirrored"
  echo
done

echo "== result =="
du -sh "$ROOT"/build/splats/*/ | sort -h
