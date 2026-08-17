#!/usr/bin/env bash
# Build the distributable zip.
#
# The shader bundle can only be produced by Unity 2021.3.45f2 on Windows, so it
# is shipped prebuilt - requiring every user to install Unity would be absurd.
# It is fetched from the Windows box unless one is already staged locally.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST="${VDGS_HOST:-user@windows-box}"
GAME='%USERPROFILE%/Downloads/Velocidrone\ Windows\ Launcher/app'

VERSION="$(grep -oE '<Version>[^<]+' "$ROOT/src/VDGS/VDGS.csproj" | head -1 | cut -d'>' -f2)"
OUT="$ROOT/build/dist/VDGS-$VERSION"
ZIP="$ROOT/build/dist/VDGS-$VERSION.zip"

echo "== building plugin =="
dotnet build "$ROOT/src/VDGS/VDGS.csproj" -c Release | tail -3

echo "== staging shader bundle =="
BUNDLE="$ROOT/dist/payload/vdgs/vdgs-shaders"
if [ ! -f "$BUNDLE" ]; then
  echo "   not staged locally, pulling from $HOST"
  mkdir -p "$(dirname "$BUNDLE")"
  scp -o BatchMode=yes -q "$HOST:$GAME/vdgs/vdgs-shaders" "$BUNDLE" 2>/dev/null
fi

# A bundle built on macOS, or without the graphics API set, loads fine at runtime
# but every shader reports isSupported=false. Size is the cheapest tripwire.
SIZE=$(stat -f%z "$BUNDLE" 2>/dev/null || stat -c%s "$BUNDLE")
if [ "$SIZE" -lt 1000000 ]; then
  echo "ERROR: vdgs-shaders is only $SIZE bytes - it was built without D3D12 or on macOS." >&2
  echo "       Rebuild it on Windows (tools/build-shaders-win.ps1)." >&2
  exit 1
fi
echo "   $SIZE bytes, ok"

echo "== assembling =="
rm -rf "$OUT" "$ZIP"
mkdir -p "$OUT/plugins" "$OUT/vdgs"

cp "$ROOT/src/VDGS/bin/Release/VDGS.dll" "$OUT/plugins/"
cp "$BUNDLE"                             "$OUT/vdgs/"
cp "$ROOT/dist/install.ps1"              "$OUT/"
cp "$ROOT/dist/README.txt"               "$OUT/"
cp "$ROOT/dist/THIRD-PARTY-NOTICES.md"   "$OUT/"

echo "== zipping =="
(cd "$(dirname "$OUT")" && zip -qr "$(basename "$ZIP")" "$(basename "$OUT")")

echo
echo "$ZIP"
ls -la "$ZIP"
echo
unzip -l "$ZIP"
