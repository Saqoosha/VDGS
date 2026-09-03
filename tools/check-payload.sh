#!/usr/bin/env bash
# Count what the staged payload carries, and stop a release that carries two of anything.
#
#   bash tools/check-payload.sh <payload-dir>
#
# Ported from the C# harness's ThePayloadCarriesOneBuild, which was deleted with the app it
# tested. It is not the same guard as emptying the directory first, and both are needed:
# `rm -rf` stops the payload accumulating across builds, this notices a payload that is
# wrong for any other reason - an interrupted `vite build`, a partial copy, a hand-edit.
#
# The failure it exists for cost five releases. Vite fingerprints every asset, so a refill
# that did not delete first left each build's files beside the last one's: a count on
# 2026-09-01 found 23 files where 5 belonged. All 23 shipped, and went out to users'
# vdgs/ui. Nothing broke, because index.html names only the current ones - which is exactly
# why nobody noticed. Counting is the only thing that sees it.
set -euo pipefail

PAY="${1:?usage: check-payload.sh <payload-dir>}"

[ -f "$PAY/BepInEx/plugins/VDGS.dll" ] || {
  echo "no VDGS.dll in $PAY - that is not a staged payload" >&2; exit 1; }

ASSETS="$PAY/vdgs/ui/assets"
[ -d "$ASSETS" ] || { echo "$PAY carries no interface (no vdgs/ui/assets)" >&2; exit 1; }

# One per entry point in web/. More than one is a build that was never swept; none means
# an entry stopped being emitted, which is its own kind of broken.
fail=0
for stem in companion- index- input- site- src-; do
  n=$(find "$ASSETS" -maxdepth 1 -name "$stem*.js" | wc -l | tr -d ' ')
  if [ "$n" != 1 ]; then
    echo "$stem*.js appears $n times in the payload - expected exactly one" >&2
    fail=1
  fi
done
[ "$fail" = 0 ] || {
  echo "the payload does not carry exactly one build. Re-stage it." >&2; exit 1; }

echo "   payload checked: one each of companion/index/input/site/src"
