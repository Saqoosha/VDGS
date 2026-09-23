#!/usr/bin/env python3
"""Refuse a viewer publish that would silently change the rest of the live site.

publish-dvr-viewer.sh is meant to change one thing, dvr/<name>/, but it deploys this
checkout's build/release/site whole. From a checkout whose site was built at another time
that reverts the catalog every companion reads; from one missing a file it removes it
live. On 2026-09-23 another session held off running it for exactly this reason.

So every top-level file here must be what is live - catalog.json byte for byte, pages with
the injected beacon removed - and so must the two a site cannot be without. A 404 on either
of those two is refused rather than read as "nothing to protect": this script only runs
against a site that exists, so a 404 there means it is looking at the wrong host. The hashed
files the front page names must be present here. The viewers are check_live_viewers.py's.

  check_live_site.py <site dir>
"""
import os
import re
import sys

from _live import fetch, missing_assets, say

site = sys.argv[1]
REQUIRED = ("catalog.json", "index.html")
bad = []

here = {f for f in os.listdir(site) if os.path.isfile(os.path.join(site, f))} if os.path.isdir(site) else set()
# What live has cannot be listed, but the files the live front page links from the site root
# can: one of those missing here - favicon.svg, say - disappears live and the page points at
# nothing. A file no page names is not worth refusing a deploy over.
front = fetch("", "/") or b""
linked = {m.decode() for m in re.findall(rb'(?:href|src)="/?([A-Za-z0-9_-][A-Za-z0-9_.-]*\.[A-Za-z0-9]+)"', front)}
for name in sorted(here | set(REQUIRED) | linked):
    path = "" if name == "index.html" else name
    live = fetch(path, "/" + path)
    local_path = os.path.join(site, name)
    local = open(local_path, "rb").read() if os.path.exists(local_path) else None
    if live is None:
        if name in REQUIRED:
            bad.append("/%s answers 404 - the check is not looking at the live site, "
                       "or the site is down" % path)
        elif local is not None:
            bad.append("/%s is not live, and this deploy would ADD it" % path)
    elif local is None:
        bad.append("/%s would be REMOVED - this checkout has no %s" % (path, name))
    elif local != live:
        bad.append("/%s would be replaced by this checkout's %s, which is not what is live"
                   % (path, name))
    else:
        say("   /%s: same as live" % path)

index = os.path.join(site, "index.html")
if os.path.exists(index):
    gone = missing_assets(open(index, "rb").read(), site)
    if gone:
        bad.append("the front page would lose files it loads: %s" % ", ".join(gone))

for line in bad:
    say("   " + line, True)
if bad:
    say("   Publish the viewer from the checkout that last published the catalog, or make", True)
    say("   this one match: rebuild the site with tools/make-catalog.sh from what is released.", True)
    sys.exit(1)
