#!/usr/bin/env python3
"""Refuse a site deploy that would silently replace or drop a live DVR viewer.

publish.sh deploys build/release/site whole, and a Worker assets deploy removes whatever
the new set does not carry. A DVR viewer page lives in that set under dvr/<name>/, put
there by make-catalog.sh from this checkout's build/dvr-viewer - a copy, and only as new as
the last publish-dvr-viewer.sh run *in this checkout*. Publishing a capture from a checkout
whose copy is older reverts the live viewer, and one with no copy removes it, with no error
anywhere. On 2026-09-23 the main checkout held a 09-22 build while a newer one was live.

Every published viewer keeps its data in R2 under dvr/<name>/data/, so the bucket listing
publish.sh already makes names them all. Each live index.html is compared with the one about
to be deployed; index.html names the hashed bundle, so any other build differs here.

  check_live_viewers.py <remote.json> <site dir> <base url>

VDGS_VIEWER_CHANGE=<name>[,<name>] lets a named viewer change on purpose.
"""
import json
import os
import random
import re
import sys
import urllib.error
import urllib.request

remote_json, site, base = sys.argv[1], sys.argv[2], sys.argv[3].rstrip("/")
allowed = {n for n in os.environ.get("VDGS_VIEWER_CHANGE", "").split(",") if n}

names = sorted({
    o["Path"].split("/")[1]
    for o in json.load(open(remote_json))
    if o["Path"].startswith("dvr/") and o["Path"].count("/") >= 2
})


# Named: the zone's bot check answers Python's default User-Agent with a 403.
UA = {"User-Agent": "vdgs-publish"}
# Cloudflare Web Analytics adds its beacon before </body> in some responses and not others
# (curl got none, urllib got one), so what is served is not always the bytes deployed.
# Removed before comparing; without this every viewer reads as changed.
INJECTED = re.compile(rb'<script[^>]*static\.cloudflareinsights\.com[^>]*></script>\n?')


def live_index(name):
    # A cache-busting query: the site's HTML is revalidated, but an edge can still hold the
    # previous copy for a moment after a deploy, and a stale answer here is a wrong verdict.
    url = "%s/dvr/%s/?cb=%d" % (base, name, random.randrange(1 << 30))
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=20) as r:
            return INJECTED.sub(b"", r.read())
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        # Anything else is "could not look", which must not pass as "nothing to protect".
        sys.exit("   dvr/%s: could not read the live page (HTTP %d) - refusing to deploy blind"
                 % (name, e.code))
    except urllib.error.URLError as e:
        sys.exit("   dvr/%s: could not reach %s (%s) - refusing to deploy blind"
                 % (name, base, e.reason))


bad = []
for name in names:
    live = live_index(name)
    if live is None:
        print("   dvr/%s: not live, nothing to protect" % name)
        continue
    local_path = os.path.join(site, "dvr", name, "index.html")
    local = open(local_path, "rb").read() if os.path.exists(local_path) else None
    if local == live:
        print("   dvr/%s: same as live" % name)
    elif name in allowed:
        print("   dvr/%s: %s - allowed by VDGS_VIEWER_CHANGE"
              % (name, "would be removed" if local is None else "changes"))
    else:
        bad.append((name, "would be REMOVED" if local is None else "would be replaced by a different build"))

for name, what in bad:
    print("   dvr/%s: the live viewer %s" % (name, what), file=sys.stderr)
if bad:
    print("   This checkout's build/dvr-viewer is not what is live. To keep the live page:", file=sys.stderr)
    for name, _ in bad:
        print("     bash tools/pull-dvr-viewer.sh %s" % name, file=sys.stderr)
    print("   then run make-catalog.sh again. To ship this copy on purpose, set", file=sys.stderr)
    print("   VDGS_VIEWER_CHANGE=%s" % ",".join(n for n, _ in bad), file=sys.stderr)
    sys.exit(1)
