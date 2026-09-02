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
# Notarization needs a notarytool keychain profile named vdgs-notary. Create once:
#   xcrun notarytool store-credentials vdgs-notary \
#     --apple-id <apple-id> --team-id VCFY2GFR89
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
[ -f "$ROOT/tools/local.env" ] && . "$ROOT/tools/local.env"

OUT="$ROOT/build/release"
VER="${1:-${VDGS_VERSION:-$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/src/VDGS/VDGS.csproj" | head -1)}}"
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

# ---------------------------------------------------------------- app
say "building the companion"
# The version the app reports lives in tauri.conf.json, so it is written there rather than
# only into the file name - otherwise every build calls itself 0.1.0 inside Get Info while
# the DMG beside it claims something else.
CONF="$ROOT/companion-tauri/src-tauri/tauri.conf.json"
python3 - "$CONF" "$VER" <<'PY'
import json, sys
path, ver = sys.argv[1], sys.argv[2]
conf = json.load(open(path))
if conf.get("version") != ver:
    conf["version"] = ver
    json.dump(conf, open(path, "w"), indent=2)
    open(path, "a").write("\n")
    print("   set tauri.conf.json version to " + ver)
PY
# Emptied first: the DMG is found by globbing this directory afterwards, and a leftover
# from an earlier version is otherwise what gets notarized and published.
rm -rf "$ROOT/companion-tauri/src-tauri/target/release/bundle"
( cd "$ROOT/companion-tauri/src-tauri" && cargo tauri build --bundles app,dmg )
APP="$ROOT/companion-tauri/src-tauri/target/release/bundle/macos/VDGS Companion.app"
DMG="$(ls "$ROOT"/companion-tauri/src-tauri/target/release/bundle/dmg/*.dmg 2>/dev/null | head -1)"
[ -d "$APP" ] || { echo "no .app produced" >&2; exit 1; }
[ -n "$DMG" ] && [ -f "$DMG" ] || { echo "no .dmg produced" >&2; exit 1; }
echo "   app ok: $APP"
echo "   dmg ok: $DMG"

# Signing is done by Tauri via APPLE_SIGNING_IDENTITY during the build above. Checked
# here so an unsigned app stops now rather than being uploaded and rejected by Apple
# several minutes later, with a DMG left behind that looks finished.
codesign --verify --deep --strict "$APP" || {
  echo "the app is not signed - set APPLE_SIGNING_IDENTITY before building" >&2
  exit 1
}
codesign -dv --verbose=2 "$APP" 2>&1 | grep -E 'Authority=|Identifier='

# ---------------------------------------------------------------- notarize
# Profile: xcrun notarytool store-credentials vdgs-notary --apple-id ... --team-id VCFY2GFR89
say "notarizing"
xcrun notarytool submit "$DMG" --keychain-profile vdgs-notary --wait
xcrun stapler staple "$DMG"

DEST="$OUT/VDGS-Companion-$VER-macos.dmg"
cp "$DMG" "$DEST"
echo "-> $DEST  ($(du -h "$DEST" | cut -f1))"

say "done"
ls -la "$OUT"/VDGS-Companion-*-macos.dmg 2>/dev/null || ls -la "$DEST"
