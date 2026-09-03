#!/usr/bin/env bash
# Package what someone else needs in order to fly this, without a checkout of the repo.
#
# Two archives, because they change at different rates and carry different rights:
#
#   vdgs-mod-<ver>.zip     the plugin DLL and the baked shader bundle. MIT, our code.
#   vdgs-scene-<name>.zip  one capture: converted splats, collision, placement. The
#                          capture's own licence travels inside it.
#
# The shader bundle can only be baked by Unity 2021.3.45f2 on Windows, so this reuses the
# one already deployed to the game box rather than trying to bake here.
#
#   bash tools/make-release.sh                       # mod only
#   bash tools/make-release.sh --scene FDF-2026-08-24 --scene-dir <path> --scene-licence CC0
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
[ -f "$ROOT/tools/local.env" ] && . "$ROOT/tools/local.env"
quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

OUT="$ROOT/build/release"
VERSION="${VDGS_VERSION:-$(date +%Y.%m.%d)}"
SCENE=""; SCENE_DIR=""; SCENE_LICENCE="CC0-1.0"; SCENE_ONLY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --scene)         SCENE="$2"; shift 2 ;;
    --scene-dir)     SCENE_DIR="$2"; shift 2 ;;
    --scene-licence) SCENE_LICENCE="$2"; shift 2 ;;
    --version)       VERSION="$2"; shift 2 ;;
    # The shader bundle only exists on the Windows box, so a scene can be cut
    # while that machine is off.
    --scene-only)    SCENE_ONLY=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT"
say() { printf '\n== %s ==\n' "$1"; }

# ---------------------------------------------------------------- mod
if [ "$SCENE_ONLY" = 0 ]; then
say "building the plugin"
# Stamped with the release version, not the placeholder in the csproj. The companion's
# mod button compares the installed version with the one it carries, so while every
# build reported the same 0.1.0.0 it could only ever offer "Reinstall mod" - it had no
# way to know an update was an update. Only this path stamps it; a dev build keeps the
# placeholder, which is what makes it recognisable as one.
dotnet build "$ROOT/src/VDGS/VDGS.csproj" -c Release -p:Version="$VERSION" | tail -2
DLL="$ROOT/src/VDGS/bin/Release/VDGS.dll"
[ -f "$DLL" ] || { echo "no VDGS.dll produced" >&2; exit 1; }

# Staged under build/ rather than in a temp dir: the companion app carries this same
# tree as its payload, so "Install mod" installs what a release would.
STAGE="$OUT"
rm -rf "$STAGE/vdgs-mod"
mkdir -p "$STAGE/vdgs-mod/BepInEx/plugins" "$STAGE/vdgs-mod/vdgs"
cp "$DLL" "$STAGE/vdgs-mod/BepInEx/plugins/"

say "fetching the shader bundle from the game box"
# Baked, not built here: Unity on macOS refuses to compile D3D shaders and produces an
# empty bundle without saying so. The size check below is the guard against shipping one.
if [ -n "${VDGS_HOST:-}" ] && scp -o BatchMode=yes -o ConnectTimeout=8 -q \
     "$VDGS_HOST:Downloads/Velocidrone\\ Windows\\ Launcher/app/vdgs/vdgs-shaders" \
     "$STAGE/vdgs-mod/vdgs/vdgs-shaders" 2>/dev/null; then
  :
elif [ -f "$ROOT/build/bundles/Windows/vdgs-shaders" ]; then
  cp "$ROOT/build/bundles/Windows/vdgs-shaders" "$STAGE/vdgs-mod/vdgs/vdgs-shaders"
else
  echo "no shader bundle available: start the game box, or run tools/bake-shaders.sh" >&2
  exit 1
fi

BUNDLE_BYTES=$(wc -c < "$STAGE/vdgs-mod/vdgs/vdgs-shaders")
if [ "$BUNDLE_BYTES" -lt 1000000 ]; then
  echo "shader bundle is only $BUNDLE_BYTES bytes - it was baked without D3D12 and every" >&2
  echo "splat shader in it is unsupported. Re-bake before releasing." >&2
  exit 1
fi
echo "   bundle ok: $BUNDLE_BYTES bytes"

say "building the control UI"
# The plugin serves this from <game>/vdgs/ui while the game runs. Without it the browser
# UI silently falls back to the short placeholder page compiled into the DLL - which
# loads, looks deliberate, and does none of what the real one does.
( cd "$ROOT/web" && bun run build ) | tail -2
[ -f "$ROOT/web/dist/index.html" ] || { echo "no web/dist produced" >&2; exit 1; }

mkdir -p "$STAGE/vdgs-mod/vdgs/ui"
cp -R "$ROOT/web/dist/." "$STAGE/vdgs-mod/vdgs/ui/"
# companion.html is the setup app's page, built from the same project. The plugin serves
# this folder to the LAN, and a setup page with no app behind it only confuses whoever
# finds it.
rm -f "$STAGE/vdgs-mod/vdgs/ui/companion.html"
echo "   ui ok: $(find "$STAGE/vdgs-mod/vdgs/ui" -type f | wc -l | tr -d ' ') files"

cat > "$STAGE/vdgs-mod/README.txt" <<EOF
VDGS $VERSION - 3D Gaussian Splatting inside VelociDrone

Copy the two folders in here over your VelociDrone app folder, so that you end up with:

    <VelociDrone>/app/BepInEx/plugins/VDGS.dll
    <VelociDrone>/app/vdgs/vdgs-shaders
    <VelociDrone>/app/vdgs/ui/

BepInEx 5.4.23.5 (win_x64) has to be installed first, and the game has to be started
with -force-d3d12. Full instructions: docs/USAGE.md in the repository.

While the game is running, the mod is driven from a browser at http://localhost:8777/
- that page is the vdgs/ui folder above. It is also reachable from another machine on
the same network, which is the point: you can fly on one screen and drive it from another.

The mod is MIT licensed. Captures are not included and carry their own terms.
EOF

MOD_ZIP="$OUT/vdgs-mod-$VERSION.zip"
rm -f "$MOD_ZIP"
( cd "$STAGE/vdgs-mod" && zip -qr "$MOD_ZIP" . )
echo "-> $MOD_ZIP  ($(du -h "$MOD_ZIP" | cut -f1))"

# ---------------------------------------------------------------- companion
# Built after the mod, not before: the app carries that same tree as its payload, so
# building it first would ship whatever the last run left behind.
#
# The work happens in make-win-app.sh, which needs a Windows box with a Rust toolchain -
# the app is Tauri now, and nothing on a Mac can link a Windows binary. It is a separate
# script for the same reason make-mac-app.sh is: one machine per target, and each is
# runnable on its own when only that half needs rebuilding.
#
# The asset-count check that used to live here is gone with the C# app. It existed because
# the csproj's Copy targets appended into a folder they never emptied, so two builds left
# 94 fingerprinted asset files where 38 were current. make-win-app.sh stages its payload
# into a directory it removes first, so there is nothing to accumulate.
if [ -n "${VDGS_WIN_BUILD_HOST:-}" ]; then
  say "building the Windows companion"
  bash "$ROOT/tools/make-win-app.sh" "$VERSION"
else
  say "skipping the Windows companion"
  # Not fatal. The mod archive above is the part that has to exist for a release, and
  # someone rebuilding only a scene should not need a second Windows machine powered on.
  # Said out loud, though: silently publishing a catalog whose Windows entry is whatever
  # zip an earlier run left in build/release is how a release ships last month's app.
  echo "   VDGS_WIN_BUILD_HOST is not set - no new vdgs-companion-$VERSION.zip will be"
  echo "   produced, and make-catalog.sh will pick the newest zip already in build/release."
fi

else
  STAGE="$OUT"
  echo "(--scene-only: no mod archive)"
fi

# ---------------------------------------------------------------- scene
if [ -n "$SCENE" ]; then
  [ -n "$SCENE_DIR" ] || { echo "--scene needs --scene-dir" >&2; exit 2; }
  [ -f "$SCENE_DIR/meta.json" ] || { echo "$SCENE_DIR has no meta.json" >&2; exit 2; }

  say "packaging scene $SCENE"
  rm -rf "$STAGE/scene"
  mkdir -p "$STAGE/scene/vdgs/$SCENE"
  cp "$SCENE_DIR"/*.bin "$SCENE_DIR"/meta.json "$STAGE/scene/vdgs/$SCENE/"
  [ -f "$SCENE_DIR/placement.json" ] && cp "$SCENE_DIR/placement.json" "$STAGE/scene/vdgs/$SCENE/"

  # A capture without its collision mesh is flown straight through, and a capture without
  # its placement lands wherever the track's origin happens to be. Both are silent in the
  # game, so they are called out here rather than discovered by whoever downloads it.
  HAVE_COLLISION=0; HAVE_PLACEMENT=0
  [ -f "$SCENE_DIR/collision.bin" ] && HAVE_COLLISION=1
  [ -f "$SCENE_DIR/placement.json" ] && HAVE_PLACEMENT=1
  if [ "$HAVE_COLLISION" = 0 ] || [ "$HAVE_PLACEMENT" = 0 ]; then
    echo
    echo "   INCOMPLETE SCENE:"
    [ "$HAVE_COLLISION" = 0 ] && echo "     no collision.bin - the capture will be flown through"
    [ "$HAVE_PLACEMENT" = 0 ] && echo "     no placement.json - it will sit at the track origin"
    echo "     Both live beside the installed scene on the game box; copy them in and re-run."
    echo
  fi

  SPLATS=$(python3 -c "import json,sys;print(f\"{json.load(open(sys.argv[1]))['splatCount']:,}\")" \
           "$SCENE_DIR/meta.json")

  # bindings.json maps a track NAME to a scene, so it only works if the track is named
  # the same on the other machine. Shipped as a sample rather than merged blindly.
  #
  # The name is looked up in catalog/ rather than written here. It used to be the literal
  # string "VDGS FDF", which was right for the first capture ever packaged and wrong for
  # every one after it, including one that shipped. Getting it wrong is the silent kind:
  # the capture installs, the track installs, the binding is written, and nothing renders,
  # with no error anywhere.
  #
  # VelociDrone form-encodes the stored name (space -> '+', literal '+' -> '%2b') and
  # bindings are keyed by the DISPLAYED name, so both stages are undone, in that order.
  TRACK_NAME=$(python3 "$ROOT/tools/track_display_name.py" "$ROOT" "$SCENE")
  echo "   sample binding: $TRACK_NAME -> $SCENE"
  python3 -c "import json,sys; json.dump({sys.argv[2]: [sys.argv[3]]}, open(sys.argv[1],'w'), indent=2)" \
          "$STAGE/scene/bindings.sample.json" "$TRACK_NAME" "$SCENE"

  cat > "$STAGE/scene/README.txt" <<EOF
$SCENE - a capture for VDGS ($SPLATS splats)

1. Copy the "vdgs" folder over <VelociDrone>/app/, giving you
       <VelociDrone>/app/vdgs/$SCENE/
2. Download the matching track in VelociDrone's Track Manager.
3. Tell the mod which track shows this capture: open http://localhost:8777/ while the
   game is running and bind it there, or merge bindings.sample.json into
   <VelociDrone>/app/vdgs/bindings.json by hand.

   The binding is by track NAME. If you renamed the track, use your name, not the one
   in the sample.

$( [ "$HAVE_PLACEMENT" = 1 ] \
   && echo "placement.json holds where the capture sits relative to the track. It is tuned to
the track above; if you build your own course, adjust it from the browser UI." \
   || echo "NOTE: no placement.json is included, so the capture starts at the track origin.
Position it from the browser UI and it will be saved for you." )

$( [ "$HAVE_COLLISION" = 1 ] \
   && echo "collision.bin makes the walls and the ground solid." \
   || echo "NOTE: no collision mesh is included - you will fly through this capture." )

Licence: $SCENE_LICENCE
EOF

  SCENE_ZIP="$OUT/vdgs-scene-$SCENE.zip"
  rm -f "$SCENE_ZIP"
  ( cd "$STAGE/scene" && zip -qr "$SCENE_ZIP" . )
  echo "-> $SCENE_ZIP  ($(du -h "$SCENE_ZIP" | cut -f1))"
fi

rm -rf "$STAGE/scene"
say "done"
ls -la "$OUT"
