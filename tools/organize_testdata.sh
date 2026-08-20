#!/usr/bin/env bash
# Sort build/testdata into folders. MOVES only - nothing is deleted, and nothing that
# another session might be holding is touched beyond its path.
#
#   scenes/    the ply each tool consumes, one per capture
#   raw/       untouched distributions, re-fetchable
#   work/      intermediates from experiments and hand edits
#   fixtures/  synthetic test assets
#
# The old paths keep working: every moved file gets a symlink left behind at its original
# location. Another session mid-measurement does not break, and the links can be swept
# once nothing references them.
set -euo pipefail
R="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
T=$R/build/testdata
cd "$T"

mkdir -p scenes raw work fixtures

move() {   # dest file...
  local dest=$1; shift
  for f in "$@"; do
    if [ -f "$f" ] && [ ! -L "$f" ]; then
      mv "$f" "$dest/$f"
      ln -s "$dest/$f" "$f"
      printf '  %-26s -> %s/\n' "$f" "$dest"
    fi
  done
}

# What the tools reference today (reprocess.sh SCENES, preview.sh scene_source).
move scenes bonsai2-aligned.ply playroom-nocrop.ply drjohnson-aligned.ply luigi.ply \
            calico-lod3.ply textilni-lod3.ply

# Distributions that can be fetched again if they are ever dropped.
move raw bonsai.ply drjohnson.ply playroom.ply nelson-full.ply utlida-full.ply

# Synthetic assets.
move fixtures testcube.ply orient.ply orient-mirrored.ply

# Everything else that is a ply: experiments, crops, hand edits, LOD and pruning outputs.
for f in *.ply; do
  [ -L "$f" ] && continue
  [ -f "$f" ] || continue
  mv "$f" "work/$f"
  ln -s "work/$f" "$f"
  printf '  %-26s -> work/\n' "$f"
done

echo
echo "== result =="
for d in scenes raw work fixtures; do
  printf '  %-9s %2d files  %s\n' "$d" "$(ls "$d" | wc -l | tr -d ' ')" \
    "$(du -sh "$d" | cut -f1)"
done
echo "  symlinks left at the old paths: $(find . -maxdepth 1 -type l | wc -l | tr -d ' ')"
