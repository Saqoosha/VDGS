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
to be deployed; index.html names the hashed bundle, so any other build differs here. The
files that index.html names must also be in the local copy, or the deploy would ship a page
whose script is gone.

  check_live_viewers.py <remote.json> <site dir>

The live site is VDGS_BASE_URL (default https://vdgs.saqoo.sh), the host the Worker deploys
to - not whatever host the catalog's links carry: a catalog built with another base URL
would make every viewer 404 there and read as "not live, nothing to protect".
VDGS_VIEWER_CHANGE=<name>[,<name>] lets a named viewer change on purpose.
"""
import json
import os
import re
import sys

from _live import fetch, missing_assets, say

remote_json, site = sys.argv[1], sys.argv[2]
allowed = {n.strip() for n in os.environ.get("VDGS_VIEWER_CHANGE", "").split(",") if n.strip()}


# Every viewer that is published (its data is in R2) and every one this deploy would ship
# (a folder here): a folder with no data behind it still goes live, and one whose data was
# kept after its page was taken down would come back.
local_dvr = os.path.join(site, "dvr")
names = sorted({
    o["Path"].split("/")[1]
    for o in json.load(open(remote_json))
    if re.match(r"dvr/[^/]+/data/", o["Path"])
} | ({d for d in os.listdir(local_dvr) if os.path.isdir(os.path.join(local_dvr, d))}
     if os.path.isdir(local_dvr) else set()))


bad = []
for name in names:
    live = fetch("dvr/%s/" % name, "dvr/%s" % name)
    local_dir = os.path.join(site, "dvr", name)
    local_path = os.path.join(local_dir, "index.html")
    local = open(local_path, "rb").read() if os.path.exists(local_path) else None
    if live is None:
        if local is None:
            say("   dvr/%s: not live, nothing to protect" % name)
        elif name in allowed:
            say("   dvr/%s: would be added - allowed by VDGS_VIEWER_CHANGE" % name)
        else:
            bad.append((name, "is not live, and this deploy would ADD it"))
        continue
    missing = missing_assets(local, local_dir)
    if local == live and not missing:
        say("   dvr/%s: same as live" % name)
    elif name in allowed:
        say("   dvr/%s: %s - allowed by VDGS_VIEWER_CHANGE"
            % (name, "would be removed" if local is None else "changes"))
    elif local is None:
        bad.append((name, "would be REMOVED"))
    elif local != live:
        bad.append((name, "would be replaced by a different build"))
    else:
        bad.append((name, "would lose files its page loads: %s" % ", ".join(missing)))

for name, what in bad:
    say("   dvr/%s: the live viewer %s" % (name, what), True)
if bad:
    say("   This checkout's build/dvr-viewer is not what is live. To keep the live page:", True)
    for name, _ in bad:
        say("     bash tools/pull-dvr-viewer.sh %s" % name, True)
    say("   then run make-catalog.sh again (it rebuilds the whole release set). To ship this", True)
    say("   copy on purpose, set VDGS_VIEWER_CHANGE=%s" % ",".join(n for n, _ in bad), True)
    sys.exit(1)
