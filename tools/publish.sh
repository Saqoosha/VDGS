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
# no multipart, which a 375 MB capture walks straight into. Credentials come from the VDGS
# 1Password Environment through its mounted .env, so nothing is typed, printed, or left on
# disk. The rclone remote is named r2 and lives in the machine's own rclone.conf.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SITE="$ROOT/build/release/site"
FILES="$ROOT/build/release/files"
BUCKET="vdgs"
REMOTE="${VDGS_R2_REMOTE:-r2}"
MOUNT="${VDGS_R2_ENV:-$HOME/.claude/1p-mounts/vdgs.env}"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT INT TERM

say() { printf '\n== %s ==\n' "$1"; }

[ -f "$SITE/catalog.json" ] || {
  echo "no $SITE/catalog.json - run tools/make-catalog.sh first" >&2; exit 1; }
command -v rclone >/dev/null || { echo "rclone is not installed" >&2; exit 1; }
rclone listremotes 2>/dev/null | grep -qx "$REMOTE:" || {
  echo "rclone has no '$REMOTE:' remote - add one for R2, or set VDGS_R2_REMOTE" >&2; exit 1; }

# ------------------------------------------------------------------ credentials
# The mount is a FIFO 1Password refills on every open, so this reads like a file and
# leaves no plaintext anywhere. Read once - it is documented as unsafe for concurrent
# readers - and with a deadline, because a stale FIFO with nobody on the other end blocks
# forever and a hang is a worse answer than an error.
CAT=(cat); command -v timeout >/dev/null && CAT=(timeout 15 cat)
[ -e "$MOUNT" ] || {
  echo "no R2 credentials at $MOUNT" >&2
  echo "  1Password app -> Developer -> View Environments -> VDGS -> Destinations" >&2
  echo "  or point VDGS_R2_ENV at the mount" >&2; exit 1; }
CREDS="$("${CAT[@]}" "$MOUNT" 2>/dev/null || true)"
[ -n "$CREDS" ] || {
  echo "$MOUNT did not produce anything - is 1Password running and unlocked?" >&2; exit 1; }
RCLONE_CONFIG_R2_ACCESS_KEY_ID="$(printf '%s\n' "$CREDS" | sed -n 's/^R2_ACCESS_KEY_ID=//p' | head -1)"
RCLONE_CONFIG_R2_SECRET_ACCESS_KEY="$(printf '%s\n' "$CREDS" | sed -n 's/^R2_SECRET_ACCESS_KEY=//p' | head -1)"
unset CREDS
[ -n "$RCLONE_CONFIG_R2_ACCESS_KEY_ID" ] && [ -n "$RCLONE_CONFIG_R2_SECRET_ACCESS_KEY" ] || {
  echo "$MOUNT has no R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY" >&2; exit 1; }
export RCLONE_CONFIG_R2_ACCESS_KEY_ID RCLONE_CONFIG_R2_SECRET_ACCESS_KEY

# The token is scoped to Object Read & Write on this one bucket, which is what it should
# be - so rclone's habit of confirming a bucket exists by trying to create it comes back
# 403. Skipping that check is the fix; widening the token is not.
RC=(rclone --s3-no-check-bucket)

# ------------------------------------------------------------------ what is being sent
# Only what the catalog names. Sending everything under files/ meant re-uploading captures
# that had already dropped out of the list, and hid which objects a publish depends on.
python3 - "$SITE/catalog.json" > "$TMP/keys.tsv" <<'PY'
import json, sys
c = json.load(open(sys.argv[1]))
def key(u): return u.split("/", 3)[3]
rows = []
for s in c["scenes"]:
    rows.append((key(s["scene"]["url"]), s["scene"]["bytes"], s["scene"]["sha256"]))
    if s.get("track"):
        rows.append((key(s["track"]["url"]), s["track"]["bytes"], s["track"]["sha256"]))
if c.get("app"):
    a = c["app"]; rows.append((key(a["url"]), a["bytes"], a["sha256"]))
for k, b, h in rows:
    print("%s\t%d\t%s" % (k, b, h))
PY
[ -s "$TMP/keys.tsv" ] || { echo "the catalog names nothing to upload" >&2; exit 1; }

say "checking what the catalog names"
# Measured against the real file before anything leaves this machine. A catalog naming a
# digest its own file does not have would install nothing on the other end, and the error
# would arrive after the download rather than before it.
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
done < "$TMP/keys.tsv"

say "asking the bucket what is already there"
# Listed once, as JSON, and parsed rather than pattern-matched: a key with a space in it
# truncates under awk. A listing that fails must not read as an empty bucket either - that
# would make every check below pass by having nothing to check.
"${RC[@]}" lsjson --recursive --metadata "$REMOTE:$BUCKET" > "$TMP/remote.json" || {
  echo "   could not list $REMOTE:$BUCKET - refusing to publish blind" >&2; exit 1; }

# Each object carries the sha256 it was uploaded with, so "is this already published" is
# answered on content rather than on length. Size alone is the wrong question: three
# companion builds on 2026-09-01 came out 6,607,301 / 6,607,540 / 6,607,546 bytes, and two
# builds landing on the same length is ordinary rather than freakish.
python3 - "$TMP/remote.json" "$TMP/keys.tsv" > "$TMP/plan.tsv" <<'PY'
import json, sys
remote = {}
for o in json.load(open(sys.argv[1])):
    if o.get("IsDir"): continue
    remote[o["Path"]] = (o["Size"], (o.get("Metadata") or {}).get("sha256"))
for line in open(sys.argv[2]):
    line = line.rstrip("\n")
    if not line: continue
    key, size, sha = line.split("\t")
    if key not in remote:
        print("%s\tsend\t%s\t" % (key, sha)); continue
    there_size, there_sha = remote[key]
    if there_sha is not None:
        if there_sha == sha:
            print("%s\tskip\t%s\t" % (key, sha))
        else:
            print("%s\tSPENT\t%s\tpublished with a different sha256" % (key, sha))
    elif there_size != int(size):
        print("%s\tSPENT\t%s\tpublished with %d bytes, not %s" % (key, sha, there_size, size))
    else:
        # Uploaded before this script recorded digests. The length agrees, which is all
        # there is to go on, so it is left alone and said out loud rather than assumed.
        print("%s\tskip-unverified\t%s\t" % (key, sha))
PY

spent=0
while IFS=$'\t' read -r key verdict sha why; do
  case "$verdict" in
    SPENT)           echo "   $key: $why" >&2; spent=1 ;;
    skip)            echo "   already published, same content: $key" ;;
    skip-unverified) echo "   already published, length agrees but predates digest tagging: $key" ;;
    send)            echo "   to send: $key" ;;
  esac
done < "$TMP/plan.tsv"
[ "$spent" = 0 ] || {
  echo "   Those names are spent. Published files are served immutable for a year, so" >&2
  echo "   replacing one leaves every edge serving the old bytes against a catalog that" >&2
  echo "   advertises the new digest. Rebuild under a new version instead." >&2
  exit 1; }

say "captures to R2"
# Uploaded before the catalog that names them: a list pointing at files that are not there
# yet is the one state worth avoiding, and it is the state a reversed order leaves behind
# every single time.
while IFS=$'\t' read -r key verdict sha why; do
  [ "$verdict" = send ] || continue
  bytes=$(wc -c < "$FILES/$key" | tr -d ' ')
  echo "   $key  ($((bytes / 1000000)) MB)"
  # Stamped with what it is, so a later run can tell this object from a rebuild of the
  # same name rather than guessing from its length.
  # -M as well as --metadata-set: without it rclone accepts the flag and writes no
  # metadata at all, so the next run has nothing to compare and quietly falls back to
  # length. Measured - the first attempt at this looked like it worked.
  "${RC[@]}" copyto "$FILES/$key" "$REMOTE:$BUCKET/$key" \
    -M --metadata-set "sha256=$sha" \
    --s3-chunk-size 64M --s3-upload-concurrency 4 --stats 30s --stats-one-line
done < "$TMP/plan.tsv"

say "confirming every named object is in the bucket"
# The deploy below publishes the list. Anything missing here would be a live link to a
# 404, so this is the last point at which that is still cheap to find.
"${RC[@]}" lsjson --recursive --metadata "$REMOTE:$BUCKET" > "$TMP/after.json" || {
  echo "   could not re-list $REMOTE:$BUCKET" >&2; exit 1; }
python3 - "$TMP/after.json" "$TMP/keys.tsv" <<'PY' || exit 1
import json, sys
remote = {}
for o in json.load(open(sys.argv[1])):
    if o.get("IsDir"): continue
    remote[o["Path"]] = (o["Size"], (o.get("Metadata") or {}).get("sha256"))
bad = 0
for line in open(sys.argv[2]):
    line = line.rstrip("\n")
    if not line: continue
    key, size, sha = line.split("\t")
    got = remote.get(key)
    if got is None:
        print("   %s is not in the bucket" % key, file=sys.stderr); bad = 1
    elif got[0] != int(size):
        print("   %s is %d bytes in the bucket, not %s" % (key, got[0], size), file=sys.stderr); bad = 1
    elif got[1] is not None and got[1] != sha:
        print("   %s carries a different sha256 in the bucket" % key, file=sys.stderr); bad = 1
sys.exit(bad)
PY
echo "   all present"

say "site and catalog"
( cd "$ROOT/worker" && npx wrangler deploy )

say "done"
echo "check it:  curl -sS -A VDGSCompanion https://vdgs.saqoo.sh/catalog.json | head -3"
