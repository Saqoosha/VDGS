#!/usr/bin/env bash
# Put the built site and the captures where the app can reach them.
#
# Two destinations because they are different sizes, not different kinds: the page and the
# catalog are a few hundred kilobytes and ship with the Worker, while a capture is
# hundreds of megabytes and goes to R2. Both answer on the same origin, so a catalog entry
# and the page listing it can never point at different places.
#
#   bash tools/make-catalog.sh --base-url https://vdgs.saqoo.sh
#   bash tools/publish.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SITE="$ROOT/build/release/site"
FILES="$ROOT/build/release/files"
BUCKET="vdgs"

[ -f "$SITE/catalog.json" ] || {
  echo "no $SITE/catalog.json - run tools/make-catalog.sh first" >&2; exit 1; }

say() { printf '\n== %s ==\n' "$1"; }

say "captures to R2"
# Uploaded before the catalog that names them: a list pointing at files that are not there
# yet is the one state worth avoiding, and it is the state a reversed order leaves behind
# every single time.
for f in "$FILES"/scene/*.zip "$FILES"/track/*.json "$FILES"/app/*.zip; do
  [ -e "$f" ] || continue
  key="$(basename "$(dirname "$f")")/$(basename "$f")"
  size=$(wc -c < "$f" | tr -d ' ')
  echo "   $key  ($((size / 1000000)) MB)"
  npx wrangler r2 object put "$BUCKET/$key" --file "$f" --remote >/dev/null
done

say "site and catalog"
( cd "$ROOT/worker" && npx wrangler deploy )

say "done"
echo "check it:  curl -sS https://vdgs.saqoo.sh/catalog.json | head -3"
