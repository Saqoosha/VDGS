#!/usr/bin/env bash
# Retrain a capture with the sky masked out, and bring the .ply back.
#
# An outdoor capture spends most of every frame on sky, and the optimiser answers that by
# parking large near-opaque gaussians above the scene. They are the "giant splats" that
# smear across the view in flight, and cutting them afterwards is a threshold fight that
# takes treetops and power lines with it. Masking the sky means they are never created,
# and MCMC recycles the budget into the ground. VelociDrone draws its own sky anyway.
#
#   bash tools/sky-mask-train.sh --preview          # 5 masks, fetched for inspection
#   bash tools/sky-mask-train.sh                    # masks, patch, train, fetch .ply
#   bash tools/sky-mask-train.sh --masks-only
#   bash tools/sky-mask-train.sh --revert           # undo the gsplat patch
#
# Host and paths come from tools/local.env (gitignored): VDGS_TRAIN_HOST, and optionally
# VDGS_TRAIN_DATA / VDGS_TRAIN_GSPLAT.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
[ -f "$ROOT/tools/local.env" ] && . "$ROOT/tools/local.env"
quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

HOST="${VDGS_TRAIN_HOST:?set VDGS_TRAIN_HOST in tools/local.env}"
DATA="${VDGS_TRAIN_DATA:-~/dgs-field}"
GSPLAT="${VDGS_TRAIN_GSPLAT:-~/gsplat}"
NAME="${VDGS_TRAIN_NAME:-results-skymask}"

PREVIEW=0; MASKS_ONLY=0; REVERT=0
for a in "$@"; do
  case "$a" in
    --preview)    PREVIEW=1 ;;
    --masks-only) MASKS_ONLY=1 ;;
    --revert)     REVERT=1 ;;
    *) echo "unknown option: $a" >&2; exit 2 ;;
  esac
done

say() { printf '\n== %s ==\n' "$1"; }

# The remote shell is bash; ship the two Python tools rather than keeping a hand-edited
# copy over there. A second copy drifting out of sync is how the shader bake script
# silently stopped working for two days.
say "shipping tools"
scp -o BatchMode=yes -q "$ROOT/tools/sky_mask_make.py" "$ROOT/tools/sky_mask_patch.py" \
    "$HOST:" 2>&1 | quiet

if [ "$REVERT" = 1 ]; then
  say "reverting gsplat patch"
  ssh -o BatchMode=yes "$HOST" "python3 ~/sky_mask_patch.py --examples-dir $GSPLAT/examples --revert" 2>&1 | quiet
  exit 0
fi

say "generating masks"
LIMIT=""
[ "$PREVIEW" = 1 ] && LIMIT="--limit 5 --overwrite"
# transformers is not part of the training env by default; installing it does not disturb
# gsplat, which is a separate wheel.
ssh -o BatchMode=yes "$HOST" "
  set -e
  export PATH=/usr/lib/wsl/lib:/usr/local/cuda/bin:\$HOME/.local/bin:\$PATH
  cd $DATA && source .venv/bin/activate
  python -c 'import transformers' 2>/dev/null || uv pip install -q transformers
  python ~/sky_mask_make.py --data-dir $DATA $LIMIT
" 2>&1 | quiet

if [ "$PREVIEW" = 1 ]; then
  say "fetching preview"
  mkdir -p "$ROOT/build/skymask-preview"
  scp -o BatchMode=yes -q "$HOST:$DATA/masks/*.png" "$ROOT/build/skymask-preview/" 2>&1 | quiet
  # Side by side with the source frame: a mask is judged on whether it left the power
  # lines and flag poles alone, and that cannot be seen in the mask on its own.
  # Derived from what was just fetched, not from a listing of the mask directory - that
  # accumulates across runs and would pair new masks with old frames.
  for f in "$ROOT/build/skymask-preview"/*.png; do
    stem="$(basename "$f" .png)"
    case "$stem" in *.src) continue ;; esac
    scp -o BatchMode=yes -q "$HOST:$DATA/images_png_df4/$stem.png" \
        "$ROOT/build/skymask-preview/$stem.src.png" 2>&1 | quiet
  done
  echo "-> $ROOT/build/skymask-preview/  (mask: black = dropped)"
  exit 0
fi

[ "$MASKS_ONLY" = 1 ] && exit 0

say "patching gsplat"
ssh -o BatchMode=yes "$HOST" "python3 ~/sky_mask_patch.py --examples-dir $GSPLAT/examples" 2>&1 | quiet

# Same knobs as the run this is meant to beat (MCMC, cap 1.5M, factor 4, 30k steps), so
# the only variable is the mask.
say "training"
ssh -o BatchMode=yes "$HOST" "
  set -e
  export PATH=/usr/lib/wsl/lib:/usr/local/cuda/bin:\$HOME/.local/bin:\$PATH
  export LD_LIBRARY_PATH=/usr/lib/wsl/lib:/usr/local/cuda/lib64:\${LD_LIBRARY_PATH:-}
  cd $DATA && source .venv/bin/activate
  cd $GSPLAT/examples
  python simple_trainer.py mcmc \
    --disable_viewer \
    --eval_steps -1 \
    --data_factor 4 \
    --strategy.cap-max 1500000 \
    --save_ply \
    --max_steps 30000 \
    --mask_dir $DATA/masks \
    --data_dir $DATA \
    --result_dir $DATA/$NAME
  ls -lh $DATA/$NAME/ply
" 2>&1 | quiet

say "fetching .ply"
mkdir -p "$ROOT/build/testdata"
scp -o BatchMode=yes -q "$HOST:$DATA/$NAME/ply/*.ply" "$ROOT/build/testdata/" 2>&1 | quiet
ls -lh "$ROOT/build/testdata"/*.ply | tail -3
echo
echo "Next: align and convert as usual (docs/alignment.ja.md), then tools/deploy.sh."
echo "Held-out PSNR is NOT comparable to an unmasked run - the model no longer explains"
echo "the sky, so it is scored against ground truth that still contains it."
