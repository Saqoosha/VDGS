#!/usr/bin/env bash
# Render one scene with our Unity renderer and with an independent WebGL renderer from
# the same camera, then subtract the two images.
#
# This exists because looking at the picture does not work. A mirrored scene, a stale
# chunk buffer and an orthographic camera each produced something that looked like a
# plausible capture, and each cost a day. A second implementation fed the same .ply is
# the only check that has actually caught anything.
#
# The reference is antimatter15/splat - a single-file WebGL viewer, fetched into build/
# rather than vendored. It follows the original 3DGS convention (right-handed, Y-down),
# so its image comes out vertically mirrored relative to Unity's; compare_renders.py
# searches the orientations rather than assuming which one, and the winner is the
# measurement.
#
#   bash tools/compare_with_webref.sh <scene> <source.ply> <camPos> [focal] [size]
#
#   scene      name under build/splats/
#   source.ply the ply the scene was converted from, BEFORE mirroring
#   camPos     x,y,z in Unity coordinates; the camera looks along +Z with +Y up
#
# e.g. bash tools/compare_with_webref.sh bonsai build/testdata/bonsai2-aligned.ply \
#           1.035,-1.145,-54.8
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${VDGS_UNITY:-/Applications/Unity/Hub/Editor/2022.3.42f1/Unity.app/Contents/MacOS/Unity}"
CHROME="${VDGS_CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
PORT="${VDGS_HTTP_PORT:-8788}"
CDP="${VDGS_CDP_PORT:-9223}"

scene="${1:?scene name}"
src="${2:?source ply}"
campos="${3:?camera position x,y,z}"
focal="${4:-1920}"
size="${5:-1024}"

WEB="$ROOT/build/webref"
OUT="$ROOT/build/views"
mkdir -p "$WEB" "$OUT"

# 1. The reference viewer. Fetched, not committed - it is someone else's MIT-licensed
#    code and only needed when running this check.
if [ ! -f "$WEB/main.js" ] || [ ! -f "$WEB/index.html" ]; then
  echo "== fetching antimatter15/splat =="
  for f in index.html main.js; do
    gh api "repos/antimatter15/splat/contents/$f" --jq .content | base64 -d > "$WEB/$f"
  done
  # The bundled camera has fx 1159.59 / fy 1164.66. That 0.4% difference is four pixels
  # at 1024 wide, which swamps the comparison against Unity's single-fov square camera.
  python3 - "$WEB/main.js" <<'PY'
import sys
p = sys.argv[1]
s = open(p).read()
anchor = "let camera = cameras[0];"
patch = anchor + """
// VDGS: ?f=<focal> forces fx == fy so the projection matches Unity's square camera.
{ const f = new URLSearchParams(location.search).get("f");
  if (f) camera = Object.assign({}, camera, { fx: +f, fy: +f }); }"""
assert anchor in s, "upstream layout changed; the camera patch no longer applies"
open(p, "w").write(s.replace(anchor, patch, 1))
PY
fi

# 2. The reference renderer must be given the SAME geometry the converter saw, which is
#    the mirrored ply - not the original.
echo "== mirroring $src =="
python3 "$ROOT/tools/align_ply.py" "$src" "$WEB/$scene.ply" --mirror y --rotate 0,0,0 \
  | grep -E "^input|bounds"

# 3. Serve it. Same origin for page and data, so no CORS.
if ! curl -sSf -o /dev/null "http://127.0.0.1:$PORT/index.html" 2>/dev/null; then
  echo "== serving $WEB on $PORT =="
  ( cd "$WEB" && python3 -m http.server "$PORT" --bind 127.0.0.1 >/dev/null 2>&1 & )
  sleep 1
fi

# 4. Headless Chrome, driven over CDP. Not --screenshot: the viewer never goes idle, so
#    --virtual-time-budget hangs instead of capturing.
if ! curl -sSf -o /dev/null "http://127.0.0.1:$CDP/json/version" 2>/dev/null; then
  echo "== launching headless chrome =="
  profile="$(mktemp -d)"
  ( "$CHROME" --headless=new --use-angle=metal --remote-debugging-port="$CDP" \
      --user-data-dir="$profile" --no-first-run about:blank >/dev/null 2>&1 & )
  sleep 4
fi

# The WebGL camera: identity rotation, translation = -position. Its Y-down convention is
# NOT compensated here - that is what the orientation search is for.
IFS=, read -r px py pz <<< "$campos"
view="[1,0,0,0,0,1,0,0,0,0,1,0,$(python3 -c "print(f'{-float('$px')},{-float('$py')},{-float('$pz')}')"),1]"

echo "== reference render =="
node "$ROOT/tools/webref_shot.mjs" \
  "http://127.0.0.1:$PORT/index.html?f=$focal&url=http://127.0.0.1:$PORT/$scene.ply#$view" \
  "$OUT/webref-$scene.png" "$size"

echo "== our render =="
"$UNITY" -batchmode -quit -projectPath "$ROOT/unity/VDGSBundler" \
  -executeMethod RenderCompare.Run \
  -vdgsScene "$ROOT/build/splats/$scene" -vdgsOutFile "$OUT/unity-$scene.png" \
  -vdgsCamPos "$campos" -vdgsCamFwd 0,0,1 -vdgsCamUp 0,1,0 \
  -vdgsFocal "$focal" -vdgsSize "$size" -logFile - 2>&1 | grep -E "^\[VDGS\]|error CS"

echo "== compare =="
python3 "$ROOT/tools/compare_renders.py" \
  "$OUT/unity-$scene.png" "$OUT/webref-$scene.png" --out "$OUT/cmp-$scene.png"
