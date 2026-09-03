#!/usr/bin/env bash
# Builds the macOS companion: stages the mod payload, builds the web UI, bundles,
# signs via Tauri, and notarizes the DMG.
#
#   bash tools/make-mac-app.sh [version]
#
# Version defaults to <Version> in src/VDGS/VDGS.csproj, or VDGS_VERSION if set.
#
# Signing uses Tauri's APPLE_SIGNING_IDENTITY (not a post-build codesign). Export it
# before running, or rely on the default below:
#   export APPLE_SIGNING_IDENTITY="Developer ID Application: Tomohiko Koyama (VCFY2GFR89)"
#
# Notarization uses the notarytool keychain profile named notarytool-profile, the same
# one Canopy submits under - same Developer ID team (VCFY2GFR89), so there is nothing to
# create here. If it is ever missing:
#   xcrun notarytool store-credentials notarytool-profile \
#     --apple-id <apple-id> --team-id VCFY2GFR89 --password <app-specific password>
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
[ -f "$ROOT/tools/local.env" ] && . "$ROOT/tools/local.env"

OUT="$ROOT/build/release"
# Today's date, the same default make-release.sh uses. It used to fall back to the csproj,
# which holds 0.1.0 as a placeholder for dev builds - so running this without an argument
# on the same day as a Windows release produced a DMG called 0.1.0 sitting beside a zip
# called 2026.09.03, and the catalog then advertised two different versions of one release.
VER="${1:-${VDGS_VERSION:-$(date +%Y.%m.%d)}}"
[ -n "$VER" ] || { echo "no version: pass one, set VDGS_VERSION, or put <Version> in VDGS.csproj" >&2; exit 1; }

# Prefer Tauri's built-in signing over a manual codesign after the fact.
export APPLE_SIGNING_IDENTITY="${APPLE_SIGNING_IDENTITY:-Developer ID Application: Tomohiko Koyama (VCFY2GFR89)}"

mkdir -p "$OUT"
say() { printf '\n== %s ==\n' "$1"; }

# ---------------------------------------------------------------- plugin
say "building the plugin"
dotnet build "$ROOT/src/VDGS/VDGS.csproj" -c Release -p:Version="$VER" | tail -2
DLL="$ROOT/src/VDGS/bin/Release/VDGS.dll"
[ -f "$DLL" ] || { echo "no VDGS.dll produced" >&2; exit 1; }

# ---------------------------------------------------------------- metal shaders
say "baking the Metal shader bundle"
# Baked every time. Reusing whatever is on disk is how a release ships the old shaders
# with new C# - the mismatch AGENTS.md warns about - and the bake is a minute against a
# build that already takes several. VDGS_SKIP_BAKE=1 is for iterating on the script itself.
BUNDLE="$ROOT/build/bundles/OSX/vdgs-shaders"
if [ -z "${VDGS_SKIP_BAKE:-}" ]; then
  mkdir -p "$(dirname "$BUNDLE")"
  /Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity \
    -batchmode -quit -nographics \
    -projectPath "$ROOT/unity/VDGSBundler" \
    -executeMethod BuildBundles.BuildMac \
    -vdgsOut "$ROOT/build/bundles/OSX" \
    -logFile "$ROOT/build/bake-mac.log"
fi
[ -f "$BUNDLE" ] || { echo "no Metal shader bundle at $BUNDLE" >&2; exit 1; }
# A bundle this small means the shaders baked as unsupported: they declare wave intrinsics,
# and a project whose graphics API is not set for the target compiles them away without
# failing. It loads fine and every shader reports isSupported=false. The Metal bundle runs
# about 437 KB; the Windows one about 1.5 MB.
BUNDLE_BYTES=$(wc -c < "$BUNDLE" | tr -d ' ')
[ "$BUNDLE_BYTES" -ge 200000 ] || {
  echo "shader bundle is only $BUNDLE_BYTES bytes - it baked as unsupported" >&2; exit 1; }
echo "   bundle ok: $BUNDLE_BYTES bytes"

# ---------------------------------------------------------------- web UI
say "building the control UI"
( cd "$ROOT/web" && bun install --frozen-lockfile && bun run build ) | tail -2
[ -f "$ROOT/web/dist/index.html" ] || { echo "no web/dist produced" >&2; exit 1; }

# ---------------------------------------------------------------- bepinex pin
say "checking the BepInEx pin"
( cd "$ROOT/companion-tauri/src-tauri" && cargo test --quiet -- --ignored the_pinned_release_is_still_there )

# ---------------------------------------------------------------- payload
# Staged under companion-tauri resources so the app ships what Install mod installs.
say "staging the mod payload"
PAY="$ROOT/companion-tauri/src-tauri/resources/mod"
rm -rf "$PAY"
mkdir -p "$PAY/BepInEx/plugins" "$PAY/vdgs/ui"
cp "$DLL" "$PAY/BepInEx/plugins/"
cp "$BUNDLE" "$PAY/vdgs/vdgs-shaders"
cp -R "$ROOT/web/dist/." "$PAY/vdgs/ui/"
# companion.html / site.html are setup-app pages; the in-game UI must not serve them.
rm -f "$PAY/vdgs/ui/companion.html" "$PAY/vdgs/ui/site.html"
cat > "$PAY/README.txt" <<EOF
VDGS mod $VER for macOS.

Installed by the companion; nothing here is meant to be copied by hand.
EOF
echo "   payload ok: $(find "$PAY" -type f | wc -l | tr -d ' ') files"
bash "$ROOT/tools/check-payload.sh" "$PAY"

# ---------------------------------------------------------------- app
say "building the companion"
# The version goes into tauri.conf.json, or the bundle keeps calling itself 0.1.0 while
# the DMG beside it is named for the day it was built.
#
# It cannot go in verbatim. Releases here are CalVer - 2026.09.03, and 2026.09.01.3 for a
# fourth build in one day - and Tauri parses that field as SemVer, which forbids a leading
# zero in a numeric identifier ("must be a semver string", measured). So the zeros come
# off and a fourth component becomes build metadata:
#
#   2026.09.03   -> 2026.9.3
#   2026.09.01.3 -> 2026.9.1+3
#
# Dropping the fourth part instead was rejected: 2026.09.01 and 2026.09.01.3 would both
# then call themselves 2026.9.1, and two different downloads claiming one version is worse
# than a version that reads oddly.
SEMVER="$(python3 "$ROOT/tools/calver_to_semver.py" "$VER")"
python3 - "$ROOT/companion-tauri/src-tauri/tauri.conf.json" "$SEMVER" <<'PY'
import json, sys
path, ver = sys.argv[1], sys.argv[2]
conf = json.load(open(path))
if conf.get("version") != ver:
    conf["version"] = ver
    json.dump(conf, open(path, "w"), indent=2)
    open(path, "a").write("\n")
PY
echo "   bundle version: $SEMVER (from $VER)"
# Emptied first: the DMG is found by globbing this directory afterwards, and a leftover
# from an earlier version is otherwise what gets notarized and published.
rm -rf "$ROOT/companion-tauri/src-tauri/target/release/bundle"
# Only the .app. The disk image is built further down, from an app that has already been
# notarized and stapled - a DMG made now would carry an app with no ticket of its own, and
# a copy dragged out of it cannot be verified offline.
( cd "$ROOT/companion-tauri/src-tauri" && cargo tauri build --bundles app )
APP="$ROOT/companion-tauri/src-tauri/target/release/bundle/macos/VDGS Companion.app"
[ -d "$APP" ] || { echo "no .app produced" >&2; exit 1; }
echo "   app ok: $APP"

# Signing is done by Tauri via APPLE_SIGNING_IDENTITY during the build above. Checked
# here so an unsigned app stops now rather than being uploaded and rejected by Apple
# several minutes later, with a DMG left behind that looks finished.
codesign --verify --deep --strict "$APP" || {
  echo "the app is not signed - set APPLE_SIGNING_IDENTITY before building" >&2
  exit 1
}
codesign -dv --verbose=2 "$APP" 2>&1 | grep -E 'Authority=|Identifier='

# ---------------------------------------------------------------- notarize
# Profile: notarytool-profile (shared with Canopy; same Developer ID team VCFY2GFR89).
#
# The app is notarized and stapled BEFORE the image is built, then the image is notarized
# too. Both halves are needed: the image's ticket is what lets someone open the download
# offline, and the app's own ticket is what survives being dragged to Applications.
#
# Everything happens in /tmp on purpose. notarytool mounts the image it is checking, and a
# mount left attached by an earlier run - or by a run that died - makes the next submit
# hang in xar_open_digest_verify with nothing reaching Apple. Canopy documents the whole
# failure; the short version is that /tmp keeps this away from Time Machine's locks, and
# the sweep below keeps it away from our own leftovers.
say "notarizing the app"
WORK="$(mktemp -d /tmp/vdgs-notarize.XXXXXX)"
cleanup() {
  # Detach before deleting: an attached image outlives its backing file, and the orphaned
  # helper then holds the next submit forever.
  for dev in $(hdiutil info | awk -v d="$WORK" '$0 ~ "image-path.*"d {f=1} f && /^\/dev\/disk/ {print $1; f=0}'); do
    hdiutil detach "$dev" -quiet 2>/dev/null || hdiutil detach "$dev" -force -quiet 2>/dev/null || true
  done
  rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

ditto -c -k --keepParent "$APP" "$WORK/app.zip"
xcrun notarytool submit "$WORK/app.zip" --keychain-profile notarytool-profile --wait
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"

say "building the disk image"
STAGE="$WORK/stage"
mkdir -p "$STAGE"
ditto "$APP" "$STAGE/VDGS Companion.app"
ln -s /Applications "$STAGE/Applications"
DMG="$WORK/VDGS-Companion-$VER-macos.dmg"
hdiutil create -volname "VDGS Companion" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
codesign --sign "$APPLE_SIGNING_IDENTITY" "$DMG"

say "notarizing the disk image"
xcrun notarytool submit "$DMG" --keychain-profile notarytool-profile --wait
xcrun stapler staple "$DMG"

# What a downloader will see, asserted rather than assumed.
spctl -a -vvv -t install "$DMG" 2>&1 | grep -q "source=Notarized Developer ID" || {
  echo "the dmg is not notarized as far as Gatekeeper is concerned" >&2; exit 1; }

DEST="$OUT/VDGS-Companion-$VER-macos.dmg"
cp "$DMG" "$DEST"
echo "-> $DEST  ($(du -h "$DEST" | cut -f1))"

say "done"
ls -la "$OUT"/VDGS-Companion-*-macos.dmg 2>/dev/null || ls -la "$DEST"
