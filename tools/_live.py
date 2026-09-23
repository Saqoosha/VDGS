"""Reading what vdgs.saqoo.sh serves, for the checks that run before a deploy.

Shared so the User-Agent, the injected-beacon strip and the "could not look is not a pass"
rule are written once - check_live_viewers.py and check_live_site.py drifting apart on any
of them would make one of them pass what the other refuses.
"""
import os
import random
import re
import sys
import urllib.error
import urllib.request

# The host the Worker deploys to, not whatever host a catalog's links carry: a catalog
# built with another base URL would make every page 404 there and read as "not live".
BASE = os.environ.get("VDGS_BASE_URL", "https://vdgs.saqoo.sh").rstrip("/")

# Named: the zone's bot check answers Python's default User-Agent with a 403.
UA = {"User-Agent": "vdgs-publish"}

# Cloudflare Web Analytics adds its beacon before </body> in some responses and not others
# (curl got none, urllib got one), so what is served is not always the bytes deployed.
# Stripped, the live page is byte-identical to what was deployed (measured 2026-09-23).
INJECTED = re.compile(rb'<script[^>]*static\.cloudflareinsights\.com[^>]*></script>\n?')

# The hashed files a page names, as the build writes them: under a base path, or relative,
# and possibly nested (assets/fonts/x.woff2). Stopping at the first / reported "assets/fonts"
# as a missing file.
ASSET = re.compile(rb'(?:/dvr/[^/"\']+/)?(assets(?:/[A-Za-z0-9_.-]+)+)')


def say(line, err=False):
    # One stream, flushed: stdout is block-buffered under a pipe, and a refusal printed to
    # stderr could otherwise land above lines that read as passing.
    print(line, file=sys.stderr if err else sys.stdout, flush=True)


def fetch(path, label):
    """The live bytes at BASE/path, beacon stripped; None for a 404. Anything else that
    stops us from looking refuses the deploy - "could not look" is never a pass."""
    # A cache-busting query: HTML is revalidated, but an edge can still hold the previous
    # copy for a moment after a deploy, and a stale answer here is a wrong verdict.
    url = "%s/%s?cb=%d" % (BASE, path, random.randrange(1 << 30))
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=20) as r:
            return INJECTED.sub(b"", r.read())
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        reason = "HTTP %d" % e.code
    except Exception as e:  # noqa: BLE001
        reason = "%s: %s" % (type(e).__name__, e)
    say("   %s: could not read %s (%s) - refusing to deploy blind" % (label, url, reason), True)
    sys.exit(1)


def missing_assets(page, folder):
    """Files `page` names that `folder` lacks - a deploy of that folder ships a page whose
    script is gone. Only what the page names directly, not what its bundle loads later."""
    return sorted({
        m.decode() for m in ASSET.findall(page or b"")
        if not os.path.isfile(os.path.join(folder, m.decode()))
    })
