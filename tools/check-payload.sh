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

# Vite names every chunk <stem>-<hash>.js, so two builds in one directory show up as one
# stem carrying two hashes. Group by stem and require a single hash each.
#
# This used to hardcode the five stem names the build emitted at the time. That list went
# stale the moment the interface was rewritten and its chunks were split differently -
# `input-` stopped being emitted and `CompanionApp-` appeared - which turned a release gate
# permanently red for a payload that was perfectly correct. A gate that always fails gets
# deleted, so this counts hashes per stem instead and never needs editing when the bundler
# changes its mind.
dupes=$(find "$ASSETS" -maxdepth 1 -name '*.js' -exec basename {} \; \
        | sed -E 's/-[A-Za-z0-9_-]+\.js$//' \
        | sort | uniq -d)
if [ -n "$dupes" ]; then
  echo "these chunks appear more than once in the payload:" >&2
  echo "$dupes" | sed 's/^/  /' >&2
  echo "the payload carries more than one build. Re-stage it." >&2
  exit 1
fi

# An entry that stopped being emitted is its own kind of broken, and the count is the only
# thing that sees it - index.html only ever names the chunks that do exist.
n=$(find "$ASSETS" -maxdepth 1 -name '*.js' | wc -l | tr -d ' ')
[ "$n" -ge 4 ] || {
  echo "the payload carries only $n javascript chunks - an entry point stopped being emitted" >&2
  exit 1; }

echo "   payload checked: $n chunks, no stem carrying two builds"
