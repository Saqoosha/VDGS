#!/usr/bin/env bash
# Build the catalog the companion app reads, and the folder that goes with it.
#
# An entry under catalog/entries/ says what a capture is; the zip that make-release.sh
# produced says how big it is and what it hashes to. This joins the two, so the sizes and
# digests in the published file are measured rather than typed - a digest that does not
# match is the app's only defence against a truncated or swapped download, and one copied
# by hand is a digest that will eventually be wrong.
#
#   bash tools/make-catalog.sh --base-url https://vdgs.saqoo.sh
#
# Output: build/release/site/, ready to upload as-is.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/build/release/site"
# The captures are kept apart from the site on purpose: the site is a deployment with a
# 25 MiB per-file ceiling, and a capture is hundreds of megabytes. Same origin at the far
# end, different destinations getting there.
FILES=""
BASE_URL=""

while [ $# -gt 0 ]; do
  case "$1" in
    --base-url) BASE_URL="${2%/}"; shift 2 ;;
    --out)      OUT="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

[ -n "$BASE_URL" ] || { echo "--base-url is required (where the files will be served from)" >&2; exit 2; }
case "$BASE_URL" in
  https://*) ;;
  # The app refuses anything else, so producing it here would only be found later.
  *) echo "--base-url must be https: the app refuses to download over plain http" >&2; exit 2 ;;
esac

FILES="${FILES:-$(dirname "$OUT")/files}"
rm -rf "$OUT" "$FILES"
mkdir -p "$OUT" "$FILES/scene" "$FILES/track" "$FILES/app"

# The page that lists all this is built from the same project as the in-game UI and the
# app's window, so a visitor who later runs the companion recognises it.
echo "== building the page =="
( cd "$ROOT/web" && bun run build ) | tail -2
[ -f "$ROOT/web/dist/site.html" ] || { echo "no web/dist/site.html produced" >&2; exit 1; }
cp "$ROOT/web/dist/site.html" "$OUT/index.html"
cp -R "$ROOT/web/dist/assets" "$OUT/assets"
[ -f "$ROOT/web/dist/favicon.svg" ] && cp "$ROOT/web/dist/favicon.svg" "$OUT/favicon.svg"
# The page asks for /assets and /catalog.json, so this folder has to be the site root -
# not a subdirectory of one.

python3 - "$ROOT" "$FILES" "$BASE_URL" <<'PY'
import hashlib, json, os, shutil, sys, datetime

root, out, base = sys.argv[1], sys.argv[2], sys.argv[3]
entries_dir = os.path.join(root, "catalog", "entries")
tracks_dir = os.path.join(root, "catalog", "tracks")
release = os.path.join(root, "build", "release")

def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def splat_count(zip_path):
    """The count the app shows, read out of the capture's own meta.json rather than
    restated in the entry - two places to write it is one place to get it wrong."""
    import zipfile
    with zipfile.ZipFile(zip_path) as z:
        for name in z.namelist():
            if name.endswith("/meta.json"):
                return int(json.loads(z.read(name))["splatCount"])
    return 0

scenes, skipped = [], []
for name in sorted(os.listdir(entries_dir)):
    if not name.endswith(".json"):
        continue
    meta = json.load(open(os.path.join(entries_dir, name)))
    install_as = meta["installAs"]

    zip_path = os.path.join(release, "vdgs-scene-%s.zip" % install_as)
    if not os.path.exists(zip_path):
        skipped.append((meta["id"], "no %s" % os.path.basename(zip_path)))
        continue

    shutil.copy2(zip_path, os.path.join(out, "scene", os.path.basename(zip_path)))
    entry = {
        "id": meta["id"],
        "name": meta["name"],
        "description": meta.get("description"),
        "author": meta.get("author"),
        "licence": meta.get("licence"),
        "captured": meta.get("captured"),
        "splats": splat_count(zip_path),
        "scene": {
            "url": "%s/scene/%s" % (base, os.path.basename(zip_path)),
            "bytes": os.path.getsize(zip_path),
            "sha256": digest(zip_path),
            "installAs": install_as,
        },
    }

    track_file = meta.get("track")
    if track_file:
        track_path = os.path.join(tracks_dir, track_file)
        if not os.path.exists(track_path):
            # Publishing the capture without the course would put a row in the app that
            # installs something nothing in the game reaches.
            skipped.append((meta["id"], "no track file %s" % track_file))
            continue
        track = json.load(open(track_path))
        shutil.copy2(track_path, os.path.join(out, "track", track_file))
        entry["track"] = {
            "url": "%s/track/%s" % (base, track_file),
            "bytes": os.path.getsize(track_path),
            "sha256": digest(track_path),
            "name": track["name"],
            "sceneId": track["scene_id"],
        }

    scenes.append(entry)

# The app itself is published alongside the captures: a page that lists things to install
# and does not offer the thing that installs them is a dead end.
# Newest by mtime, not by name. Sorting by name reads "2026.09.01.1" as older than
# "2026.09.01", because '1' sorts before the 'z' of ".zip" - so a second build of the same
# day silently published the first one. Whichever was built last is the one that was meant,
# and only that one is copied: hashing every build to keep one was pure waste.
builds = [n for n in os.listdir(release)
          if n.startswith("vdgs-companion-") and n.endswith(".zip")]
app = None
if builds:
    name = max(builds, key=lambda n: os.path.getmtime(os.path.join(release, n)))
    path = os.path.join(release, name)
    shutil.copy2(path, os.path.join(out, "app", name))
    app = {
        "version": name[len("vdgs-companion-"):-len(".zip")],
        "url": "%s/app/%s" % (base, name),
        "bytes": os.path.getsize(path),
        "sha256": digest(path),
    }

catalog = {
    "formatVersion": 1,
    # Stamped from the clock rather than from a file's mtime: this says when the list was
    # built, which is the question someone looking at a stale mirror is asking.
    "updated": datetime.datetime.now(datetime.timezone.utc)
                .replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    "scenes": scenes,
}
if app:
    catalog["app"] = app
# The catalog is part of the site, not of the files it points at.
with open(os.path.join(os.path.dirname(out), "site", "catalog.json"), "w") as f:
    json.dump(catalog, f, indent=2)
    f.write("\n")

for id_, why in skipped:
    print("   skipped %s: %s" % (id_, why))
total = sum(e["scene"]["bytes"] + e.get("track", {}).get("bytes", 0) for e in scenes)
print("   %d capture(s), %.1f MB to upload" % (len(scenes), total / 1e6))
PY

# Read back what was written, from a different piece of code than the one that wrote it.
# Everything in this file decides what an app downloads and unpacks, and a field that
# quietly went missing would only surface as a failed install on someone else's machine.
python3 - "$OUT/catalog.json" <<'CHECK'
import json, sys
c = json.load(open(sys.argv[1]))
assert c["formatVersion"] == 1, "format version"
for s in c["scenes"]:
    for key in ("id", "name", "splats", "scene"):
        assert s.get(key) not in (None, ""), "%s: missing %s" % (s.get("id"), key)
    for part in ("scene", "track"):
        f = s.get(part)
        if f is None:
            continue
        assert f["url"].startswith("https://"), "%s: %s is not https" % (s["id"], part)
        assert len(f["sha256"]) == 64, "%s: %s digest is not a sha256" % (s["id"], part)
        assert f["bytes"] > 0, "%s: %s has no size" % (s["id"], part)
    assert s["scene"]["installAs"], "%s: nowhere to install" % s["id"]
app = c.get("app")
if app:
    assert app["url"].startswith("https://"), "app is not https"
    assert len(app["sha256"]) == 64, "app digest is not a sha256"
    assert app["bytes"] > 0, "app has no size"
else:
    print("   note: no companion build in this catalog")
print("   catalog.json checks out")
CHECK

echo
echo "-> $OUT  (the site: deploy this)"
find "$OUT" -type f | sed "s|$OUT|   .|" | head -6
echo "   ... $(find "$OUT" -type f | wc -l | tr -d ' ') files"
echo "-> $FILES  (the captures: these go to object storage)"
find "$FILES" -type f | sed "s|$FILES|   .|"
echo
echo "bash tools/publish.sh does both."
