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
#
# Uploads with rclone rather than wrangler: wrangler refuses anything over 300 MiB and has
# no multipart, which a 375 MB capture walks straight into. Credentials come from the
# VDGS 1Password Environment through its mounted .env, so nothing is typed, printed, or
# left on disk.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SITE="$ROOT/build/release/site"
FILES="$ROOT/build/release/files"
BUCKET="vdgs"
MOUNT="${VDGS_R2_ENV:-$HOME/.claude/1p-mounts/vdgs.env}"

[ -f "$SITE/catalog.json" ] || {
  echo "no $SITE/catalog.json - run tools/make-catalog.sh first" >&2; exit 1; }
command -v rclone >/dev/null || { echo "rclone is not installed" >&2; exit 1; }

# The mount is a FIFO 1Password refills on every open, so this reads like a file and
# leaves no plaintext anywhere. Read once: it is documented as not safe for concurrent
# readers.
[ -e "$MOUNT" ] || {
  echo "no R2 credentials at $MOUNT" >&2
  echo "  1Password app -> Developer -> View Environments -> VDGS -> Destinations" >&2
  echo "  or point VDGS_R2_ENV at the mount" >&2; exit 1; }
set -a; . "$MOUNT"; set +a
[ -n "${R2_ACCESS_KEY_ID:-}" ] && [ -n "${R2_SECRET_ACCESS_KEY:-}" ] || {
  echo "$MOUNT has no R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY" >&2; exit 1; }
export RCLONE_CONFIG_R2_ACCESS_KEY_ID="$R2_ACCESS_KEY_ID"
export RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="$R2_SECRET_ACCESS_KEY"

# The token is scoped to Object Read & Write on this one bucket, which is what it should
# be - so rclone's habit of confirming a bucket exists by trying to create it comes back
# 403. Skipping that check is the fix; widening the token is not.
RC=(rclone --s3-no-check-bucket)

say() { printf '\n== %s ==\n' "$1"; }

# ------------------------------------------------------------------ what is being sent
# Only what the catalog names. Sending everything in files/ meant re-uploading captures
# that had already dropped out of the list, and hid which objects the publish depends on.
KEYS="$(python3 - "$SITE/catalog.json" <<'PY'
import json, sys
c = json.load(open(sys.argv[1]))
def key(u): return u.split("/", 3)[3]
out = []
for s in c["scenes"]:
    out.append((key(s["scene"]["url"]), s["scene"]["bytes"], s["scene"]["sha256"]))
    if s.get("track"):
        out.append((key(s["track"]["url"]), s["track"]["bytes"], s["track"]["sha256"]))
if c.get("app"):
    a = c["app"]; out.append((key(a["url"]), a["bytes"], a["sha256"]))
for k, b, h in out:
    print("%s\t%d\t%s" % (k, b, h))
PY
)"
[ -n "$KEYS" ] || { echo "the catalog names nothing to upload" >&2; exit 1; }

say "checking what the catalog names"
# Measured against the real file before anything leaves this machine. A catalog that
# names a digest its own file does not have would install nothing on the other end, and
# the error would arrive after the download rather than before it.
while IFS=$'\t' read -r key bytes sha; do
  f="$FILES/$key"
  [ -f "$f" ] || { echo "   MISSING  $key" >&2; exit 1; }
  got=$(wc -c < "$f" | tr -d ' ')
  [ "$got" = "$bytes" ] || {
    echo "   SIZE     $key: catalog says $bytes, file is $got" >&2; exit 1; }
  have=$(shasum -a 256 "$f" | cut -d' ' -f1)
  [ "$have" = "$sha" ] || {
    echo "   DIGEST   $key does not match the catalog" >&2; exit 1; }
  echo "   ok  $key  ($((bytes / 1000000)) MB)"
done <<< "$KEYS"

say "checking nothing is being overwritten with different bytes"
# Published files are served immutable for a year, so a name that is already taken must
# keep the bytes it was taken with. Overwriting one leaves every edge serving the old
# content against a catalog that advertises the new digest - which is a download that
# completes and then refuses to install. Bump the version instead; that is what happened
# to vdgs-companion-2026.09.01.zip on 2026-09-01.
REMOTE="$("${RC[@]}" ls "r2:$BUCKET" 2>/dev/null || true)"
while IFS=$'\t' read -r key bytes sha; do
  there=$(printf '%s\n' "$REMOTE" | awk -v k="$key" '$2 == k { print $1 }')
  if [ -n "$there" ] && [ "$there" != "$bytes" ]; then
    echo "   $key is already published with $there bytes, not $bytes." >&2
    echo "   That name is spent. Rebuild under a new version rather than replacing it." >&2
    exit 1
  fi
  [ -n "$there" ] && echo "   already there, unchanged: $key"
done <<< "$KEYS"

say "captures to R2"
# Uploaded before the catalog that names them: a list pointing at files that are not there
# yet is the one state worth avoiding, and it is the state a reversed order leaves behind
# every single time.
while IFS=$'\t' read -r key bytes sha; do
  there=$(printf '%s\n' "$REMOTE" | awk -v k="$key" '$2 == k { print $1 }')
  [ "$there" = "$bytes" ] && continue
  echo "   $key  ($((bytes / 1000000)) MB)"
  "${RC[@]}" copyto "$FILES/$key" "r2:$BUCKET/$key" \
    --s3-chunk-size 64M --s3-upload-concurrency 4 --stats 30s --stats-one-line
done <<< "$KEYS"

say "confirming every named object is in the bucket"
# The deploy below publishes the list. Anything missing here would be a live link to a
# 404, so this is the last point at which that is still cheap to find.
REMOTE="$("${RC[@]}" ls "r2:$BUCKET" 2>/dev/null || true)"
while IFS=$'\t' read -r key bytes sha; do
  there=$(printf '%s\n' "$REMOTE" | awk -v k="$key" '$2 == k { print $1 }')
  [ "$there" = "$bytes" ] || { echo "   $key is not in the bucket at $bytes bytes" >&2; exit 1; }
done <<< "$KEYS"
echo "   all present"

say "site and catalog"
( cd "$ROOT/worker" && npx wrangler deploy )

say "done"
echo "check it:  curl -sS -A VDGSCompanion https://vdgs.saqoo.sh/catalog.json | head -3"
