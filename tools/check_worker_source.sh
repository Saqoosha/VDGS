#!/usr/bin/env bash
# Refuse a deploy that would put a Worker other than origin/master's live.
#
# `wrangler deploy` ships this checkout's worker/ - the routing that sends /scene/, /track/,
# /app/ and /dvr/<name>/data/ to R2 - along with the site. publish.sh and
# publish-dvr-viewer.sh are about captures and viewers, so a checkout on an old or unmerged
# branch would revert the live routing as a side effect: from before b422d88 there is no
# /dvr/<name>/data/ route, and every viewer's data 404s. Which commit the live Worker came
# from cannot be read back, so origin/master is taken as what it should be: worker/ here
# must match it exactly, uncommitted edits and untracked files included.
#
#   VDGS_WORKER_CHANGE=1   deploy this checkout's Worker on purpose
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Fetched first: a stale origin/master would compare against what master used to be.
git -C "$ROOT" fetch -q origin master || {
  echo "   worker/: could not fetch origin/master - refusing to deploy blind" >&2; exit 1; }

changed="$( { git -C "$ROOT" diff --name-only origin/master -- worker/
              git -C "$ROOT" ls-files --others --exclude-standard -- worker/; } | sort -u)"
if [ -z "$changed" ]; then
  echo "   worker/: same as origin/master"
  exit 0
fi
if [ "${VDGS_WORKER_CHANGE:-}" = 1 ]; then
  echo "   worker/: differs from origin/master - allowed by VDGS_WORKER_CHANGE=1"
  printf '     %s\n' $changed
  exit 0
fi
echo "   worker/: this checkout's Worker is not origin/master's, and the deploy would put it live:" >&2
printf '     %s\n' $changed >&2
echo "   Deploy from a checkout of origin/master, or set VDGS_WORKER_CHANGE=1 to ship this one." >&2
exit 1
