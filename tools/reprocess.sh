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
QUALITY="${VDGS_QUALITY:-VeryHigh}"
# Palette-compressed spherical harmonics. Measured on the RTX 3060 (the machine that
# matters - an M1 Max hides this behind unified memory): drjohnson's splat cost falls
# 48%, the whole frame 34%, for a mean pixel difference of 1.58/255. Geometry stays at
# full Float32 precision because the preset is applied first and only SH is overridden.
SH_FORMAT="${VDGS_SH_FORMAT:-Cluster16k}"

# scene:source  — sources are SuperSplat exports unless noted
SCENES=(
  "bonsai:bonsai2-aligned.ply"
  "playroom:playroom-aligned.ply"
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
      -vdgsShFormat "$SH_FORMAT" \
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
