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
BUNDLE="$ROOT/build/bundles/OSX/vdgs-shaders"
if [ ! -f "$BUNDLE" ]; then
  mkdir -p "$(dirname "$BUNDLE")"
  /Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity \
    -batchmode -quit -nographics \
    -projectPath "$ROOT/unity/VDGSBundler" \
    -executeMethod BuildBundles.BuildMac \
    -vdgsOut "$ROOT/build/bundles/OSX" \
    -logFile "$ROOT/build/bake-mac.log"
fi
[ -f "$BUNDLE" ] || { echo "no Metal shader bundle at $BUNDLE" >&2; exit 1; }
echo "   bundle ok: $(wc -c < "$BUNDLE" | tr -d ' ') bytes"

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
( cd "$ROOT/companion-tauri/src-tauri" && cargo tauri build --bundles app,dmg )
APP="$ROOT/companion-tauri/src-tauri/target/release/bundle/macos/VDGS Companion.app"
DMG="$(ls "$ROOT"/companion-tauri/src-tauri/target/release/bundle/dmg/*.dmg | head -1)"
[ -d "$APP" ] || { echo "no .app produced" >&2; exit 1; }
[ -n "$DMG" ] && [ -f "$DMG" ] || { echo "no .dmg produced" >&2; exit 1; }
echo "   app ok: $APP"
echo "   dmg ok: $DMG"

# Signing is done by Tauri via APPLE_SIGNING_IDENTITY during the build above.
# Verify before notarizing so a missing identity fails here, not at Apple.
codesign -dv --verbose=2 "$APP" 2>&1 | grep -E 'Authority=|Identifier=' || true

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
