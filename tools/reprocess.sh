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

  ( cd "$CONVERTER" && "$UNITY" -batchmode -quit -nographics \
      -projectPath "$CONVERTER" \
      -executeMethod PlyExporter.Run \
      -vdgsInput "$mirrored" \
      -vdgsOutput "$ROOT/build/splats/$name" \
      -vdgsQuality "$QUALITY" \
      -logFile - 2>&1 | grep -E "\[VDGS\] export|fatal|error CS" ) || true

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
