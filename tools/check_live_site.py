#!/usr/bin/env python3
"""Refuse a viewer publish that would silently replace the rest of the live site.

publish-dvr-viewer.sh is meant to change one thing, dvr/<name>/, but it deploys this
checkout's build/release/site whole - the catalog and the front page included. From a
checkout whose site was built at another time that reverts the catalog every companion
reads, and from one with no site at all it would take the front page down. On 2026-09-23
another session held off running it for exactly this reason.

So everything but the viewer being published has to be what is live: catalog.json byte
for byte (the Worker serves it as deployed), the front page with the injected beacon
removed, and the hashed files that page names present here. The other viewers are
check_live_viewers.py's job, run beside this.

  check_live_site.py <site dir>
"""
import os
import sys

from _live import fetch, missing_assets, say

site = sys.argv[1]
bad = []

for path, name in (("catalog.json", "catalog.json"), ("", "index.html")):
    live = fetch(path, "/" + path)
    local_path = os.path.join(site, name)
    local = open(local_path, "rb").read() if os.path.exists(local_path) else None
    if live is None:
        say("   /%s: not live, nothing to protect" % path)
    elif local is None:
        bad.append("/%s would be REMOVED - this checkout has no %s" % (path, name))
    elif local != live:
        bad.append("/%s would be replaced by this checkout's %s, which is not what is live" % (path, name))
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
