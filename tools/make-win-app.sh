#!/usr/bin/env bash
# Builds the Windows companion: stages the mod payload, builds the web UI, ships the
# source to a Windows box, builds there, and brings back the zip a release publishes.
#
#   bash tools/make-win-app.sh [version]
#
# Version defaults to today, the same as make-release.sh and make-mac-app.sh. Passing
# different ones on the same day is how a release ends up advertising two versions.
#
# Two machines, and they are not the same machine:
#
#   VDGS_HOST            the game box. Has VelociDrone and Unity, bakes the D3D12
#                        shader bundle. No Rust toolchain.
#   VDGS_WIN_BUILD_HOST  a Windows box with Rust (x86_64-pc-windows-msvc), Visual
#                        Studio's linker, and cargo-tauri. Builds the app.
#
# It builds with --no-bundle and zips the plain exe beside its resources, rather than
# producing the NSIS installer the config can also make. The zip keeps the name and the
# shape make-catalog.sh already looks for, so nothing downstream of here changes. An
# unsigned installer would not clear SmartScreen either, so it buys nothing today.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
. "$ROOT/tools/_remote.sh"
quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

: "${VDGS_WIN_BUILD_HOST:?set VDGS_WIN_BUILD_HOST in tools/local.env - the game box has no Rust toolchain}"
BUILD_HOST="$VDGS_WIN_BUILD_HOST"

OUT="$ROOT/build/release"
VER="${1:-${VDGS_VERSION:-$(date +%Y.%m.%d)}}"

mkdir -p "$OUT"
say() { printf '\n== %s ==\n' "$1"; }

# ---------------------------------------------------------------- plugin
say "building the plugin"
dotnet build "$ROOT/src/VDGS/VDGS.csproj" -c Release -p:Version="$VER" | tail -2
DLL="$ROOT/src/VDGS/bin/Release/VDGS.dll"
[ -f "$DLL" ] || { echo "no VDGS.dll produced" >&2; exit 1; }

# ---------------------------------------------------------------- d3d12 shaders
say "fetching the D3D12 shader bundle"
# Not baked here. macOS Unity refuses to compile D3D shaders - "DXC: can only use DXC to
# target D3D from the Windows Editor" - and produces a bundle anyway, without saying so.
# Same two sources make-release.sh uses, in the same order.
BUNDLE="$ROOT/build/bundles/Windows/vdgs-shaders"
mkdir -p "$(dirname "$BUNDLE")"
if [ -n "${VDGS_HOST:-}" ] && scp -o BatchMode=yes -o ConnectTimeout=8 -q \
     "$VDGS_HOST:Downloads/Velocidrone\\ Windows\\ Launcher/app/vdgs/vdgs-shaders" \
     "$BUNDLE" 2>/dev/null; then
  echo "   from the game box"
elif [ -f "$BUNDLE" ]; then
  echo "   from build/bundles/Windows (the game box is not up)"
else
  echo "no D3D12 shader bundle: start the game box, or run tools/bake-shaders.sh" >&2
  exit 1
fi

# The single most expensive mistake this script can make, so it is checked rather than
# trusted. The splat shaders declare "#pragma require wavebasic/waveballot" and a project
# whose graphics API is not D3D12 compiles them away without failing: the bundle loads,
# and every shader reports isSupported=false. Nothing in the game or its logs says why.
# D3D12 runs about 1.5 MB. The Metal bundle - which lives one directory over and is what
# make-mac-app.sh stages into this same payload folder - is about 437 KB.
BUNDLE_BYTES=$(wc -c < "$BUNDLE" | tr -d ' ')
[ "$BUNDLE_BYTES" -ge 1000000 ] || {
  echo "shader bundle is only $BUNDLE_BYTES bytes - that is the Metal bundle, or it baked" >&2
  echo "unsupported. Either way every splat shader in it is dead. Re-bake before releasing." >&2
  exit 1; }
echo "   bundle ok: $BUNDLE_BYTES bytes"

# ---------------------------------------------------------------- web UI
say "building the control UI"
( cd "$ROOT/web" && bun install --frozen-lockfile && bun run build ) | tail -2
[ -f "$ROOT/web/dist/index.html" ] || { echo "no web/dist produced" >&2; exit 1; }

# ---------------------------------------------------------------- payload
say "staging the mod payload"
# Emptied first, always. This directory is shared with make-mac-app.sh, which stages the
# Metal bundle into it - so a payload left by the last macOS release is exactly the wrong
# one, and the size check above is the only thing standing between that and a Windows
# build that installs dead shaders.
PAY="$ROOT/companion-tauri/src-tauri/resources/mod"
rm -rf "$PAY"
mkdir -p "$PAY/BepInEx/plugins" "$PAY/vdgs/ui"
cp "$DLL" "$PAY/BepInEx/plugins/"
cp "$BUNDLE" "$PAY/vdgs/vdgs-shaders"
cp -R "$ROOT/web/dist/." "$PAY/vdgs/ui/"
# companion.html / site.html are the setup app's own pages. The plugin serves this folder
# to the LAN while the game runs, and a setup page with no app behind it only confuses
# whoever finds it.
rm -f "$PAY/vdgs/ui/companion.html" "$PAY/vdgs/ui/site.html"
cat > "$PAY/README.txt" <<EOF
VDGS mod $VER for Windows.

Installed by the companion; nothing here is meant to be copied by hand.
EOF
echo "   payload ok: $(find "$PAY" -type f | wc -l | tr -d ' ') files"

# ---------------------------------------------------------------- version
# Same mapping make-mac-app.sh uses, and it has to be the same value: releases here are
# CalVer, Tauri parses this field as SemVer, and SemVer forbids a leading zero.
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

# ---------------------------------------------------------------- ship
say "shipping the source to $BUILD_HOST"
WORK="$(mktemp -d /tmp/vdgs-win-app.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT INT TERM

# The remote gets web/dist rather than the web project, and a config with the frontend
# commands removed, so the build box needs no bun and no node. That is not tidiness: a
# beforeBuildCommand runs through cmd.exe, and on a box whose PATH is over cmd's 8191
# character limit the child receives a truncated one and cannot find bun at all. The
# failure reads as a missing tool rather than a truncated variable.
mkdir -p "$WORK/companion-tauri/src-tauri" "$WORK/web"
cp -R "$ROOT/web/dist" "$WORK/web/dist"
for item in src capabilities icons resources Cargo.toml Cargo.lock build.rs; do
  cp -R "$ROOT/companion-tauri/src-tauri/$item" "$WORK/companion-tauri/src-tauri/$item"
done
python3 - "$ROOT/companion-tauri/src-tauri/tauri.conf.json" \
          "$WORK/companion-tauri/src-tauri/tauri.conf.json" <<'PY'
import json, sys
conf = json.load(open(sys.argv[1]))
conf.setdefault("build", {})["beforeBuildCommand"] = ""
conf["build"]["beforeDevCommand"] = ""
json.dump(conf, open(sys.argv[2], "w"), indent=2)
open(sys.argv[2], "a").write("\n")
PY

# COPYFILE_DISABLE, or macOS tar writes an AppleDouble ._file beside every entry. Tauri
# then reads capabilities/._default.json, fails with "stream did not contain valid UTF-8",
# and the build stops on a file nobody wrote.
TAR="$WORK/companion-win.tgz"
COPYFILE_DISABLE=1 tar czf "$TAR" -C "$WORK" companion-tauri web

ssh -o BatchMode=yes "$BUILD_HOST" \
  "New-Item -ItemType Directory -Force -Path $REMOTE_ROOT_PS | Out-Null" 2>&1 | quiet
scp -o BatchMode=yes -q "$TAR" "$BUILD_HOST:$REMOTE_ROOT/companion-win.tgz" 2>&1 | quiet

# ---------------------------------------------------------------- build
say "building on $BUILD_HOST"
# A short PATH is built here rather than inherited. cargo-tauri shells out through
# cmd.exe, whose limit is 8191 characters, and a box whose PATH exceeds that hands its
# children a truncated one - far enough truncated that powershell.exe itself goes missing.
# The user's environment is not touched; this only shapes what this build sees.
ssh -o BatchMode=yes "$BUILD_HOST" "
  \$ErrorActionPreference = 'Stop'
  \$root = $REMOTE_ROOT_PS
  \$work = Join-Path \$root 'companion-win'
  Remove-Item -Recurse -Force \$work -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force -Path \$work | Out-Null
  Set-Location \$work
  tar xzf (Join-Path \$root 'companion-win.tgz')
  \$env:PATH = \"\$env:USERPROFILE\\.cargo\\bin;C:\\Windows\\system32;C:\\Windows;C:\\Windows\\System32\\Wbem;C:\\Windows\\System32\\WindowsPowerShell\\v1.0\"
  Set-Location (Join-Path \$work 'companion-tauri\\src-tauri')
  cargo-tauri.exe build --no-bundle
  if (\$LASTEXITCODE -ne 0) { throw 'cargo tauri build failed' }
" 2>&1 | quiet | grep -E "Compiling vdgs-companion|Finished|Built application|^error|warning:" || true

# ---------------------------------------------------------------- package
say "packaging"
# VDGS.exe, not vdgs-companion.exe: that is the name the C# companion shipped under, it is
# what the site and every existing shortcut say, and the zip is a straight replacement.
#
# resources/ travels with it. cargo tauri build --no-bundle writes the declared resources
# beside the exe in target/release, and resolve_resource_dir looks there first - without
# that folder the app opens and says it carries no mod payload.
ssh -o BatchMode=yes "$BUILD_HOST" "
  \$ErrorActionPreference = 'Stop'
  \$rel = Join-Path $REMOTE_ROOT_PS 'companion-win\\companion-tauri\\src-tauri\\target\\release'
  \$exe = Join-Path \$rel 'vdgs-companion.exe'
  if (-not (Test-Path \$exe)) { throw 'no vdgs-companion.exe produced' }
  \$res = Join-Path \$rel 'resources'
  if (-not (Test-Path \$res)) { throw 'no resources beside the exe - the app would carry no payload' }
  \$stage = Join-Path $REMOTE_ROOT_PS 'companion-win-stage'
  Remove-Item -Recurse -Force \$stage -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force -Path \$stage | Out-Null
  Copy-Item \$exe (Join-Path \$stage 'VDGS.exe')
  Copy-Item -Recurse \$res (Join-Path \$stage 'resources')
  \$zip = Join-Path $REMOTE_ROOT_PS 'vdgs-companion.zip'
  Remove-Item \$zip -ErrorAction SilentlyContinue
  Compress-Archive -Path (Join-Path \$stage '*') -DestinationPath \$zip
  'zip: ' + (Get-Item \$zip).Length
" 2>&1 | quiet

DEST="$OUT/vdgs-companion-$VER.zip"
rm -f "$DEST"
scp -o BatchMode=yes -q "$BUILD_HOST:$REMOTE_ROOT/vdgs-companion.zip" "$DEST" 2>&1 | quiet
[ -f "$DEST" ] || { echo "nothing came back from $BUILD_HOST" >&2; exit 1; }

# What came back is checked here, because everything above this line can succeed while
# the zip is still wrong - the exe from an older build, or a payload staged for macOS.
say "checking what came back"
python3 - "$DEST" "$BUNDLE_BYTES" <<'PY'
import sys, zipfile
path, want = sys.argv[1], int(sys.argv[2])
z = zipfile.ZipFile(path)
names = z.namelist()
def need(suffix):
    hit = [n for n in names if n.replace("\\", "/").endswith(suffix)]
    if not hit:
        sys.exit("the zip has no %s" % suffix)
    return hit[0]
need("VDGS.exe")
need("mod/BepInEx/plugins/VDGS.dll")
need("mod/vdgs/ui/index.html")
shaders = need("mod/vdgs/vdgs-shaders")
got = z.getinfo(shaders).file_size
if got != want:
    sys.exit("the shader bundle in the zip is %d bytes, not the %d that was staged" % (got, want))
print("   VDGS.exe, the plugin, and a %d byte D3D12 bundle" % got)
PY
echo "-> $DEST  ($(du -h "$DEST" | cut -f1))"

say "done"
