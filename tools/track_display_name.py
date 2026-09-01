#!/usr/bin/env python3
"""Print the DISPLAYED VelociDrone track name bound to a capture, for make-release.sh.

bindings.json is keyed by what the game shows, while a track file stores the name
form-encoded - a space becomes '+', a literal '+' becomes '%2b'. Undo '+' FIRST, then
'%XX'. The other order turns '%2b' into a '+' that the next stage then reads as a space,
which silently mangles every name containing a literal plus.

Prints a loud placeholder when the catalog does not know this capture, so a hand-merged
sample fails visibly instead of binding a track nobody has.
"""
import glob
import json
import os
import sys
import urllib.parse

root, scene = sys.argv[1], sys.argv[2]

for path in sorted(glob.glob(os.path.join(root, "catalog", "entries", "*.json"))):
    entry = json.load(open(path))
    if entry.get("installAs") != scene or not entry.get("track"):
        continue
    track = os.path.join(root, "catalog", "tracks", entry["track"])
    if os.path.exists(track):
        stored = json.load(open(track))["name"]
        print(urllib.parse.unquote(stored.replace("+", " ")))
        sys.exit()
    break

print("REPLACE-WITH-THE-TRACK-NAME-IN-VELOCIDRONE")
