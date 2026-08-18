#!/usr/bin/env bash
# Package the shader project, ship it to the Windows box, and bake the AssetBundle there.
#
# Shaders cannot be compiled at runtime and must come from a bundle built with the game's
# own Unity version, so any change under unity/VDGSBundler/Assets/VDGS/Shaders/ needs this
# before the game sees it. Skipping it leaves the game running old shaders against new C#,
# which fails in whatever way the mismatch happens to produce.
#
# macOS cannot do this: "DXC: can only use DXC to target D3D from the Windows Editor".
#
#   bash tools/bake-shaders.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST="${VDGS_HOST:-user@windows-box}"

quiet() { grep -vE "WARNING: |store now, decrypt later|may need to be upgraded|openssh.com/pq" || true; }

echo "== stopping the game (it holds the bundle file) =="
ssh -o BatchMode=yes "$HOST" \
  'if (Get-Process velocidrone -EA SilentlyContinue) { Stop-Process -Name velocidrone -Force; "stopped" } else { "not running" }' \
  2>&1 | quiet

echo "== packaging =="
TAR="$(mktemp -t vdgs-bundler).tgz"
# build-shaders-win.ps1 untars into %USERPROFILE%, so the archive has to carry the project
# directory itself. Packing its *contents* instead fails with "unpack failed: VDGSBundler
# missing" - which reads like a transfer problem rather than a layout one.
tar -czf "$TAR" -C "$ROOT/unity" \
    --exclude='Library' --exclude='Temp' --exclude='Logs' --exclude='obj' \
    VDGSBundler/Assets VDGSBundler/Packages VDGSBundler/ProjectSettings
echo "   $(du -h "$TAR" | cut -f1)"

scp -o BatchMode=yes -q "$TAR" "$HOST:%USERPROFILE%/vdgs-bundler.tgz" 2>&1 | quiet
scp -o BatchMode=yes -q "$ROOT/tools/build-shaders-win.ps1" "$HOST:%USERPROFILE%/build-shaders-win.ps1" 2>&1 | quiet
rm -f "$TAR"

echo "== baking =="
ssh -o BatchMode=yes "$HOST" "powershell -ExecutionPolicy Bypass -File %USERPROFILE%\\build-shaders-win.ps1" \
  2>&1 | quiet | grep -E "\[VDGS\] (built|building)|Shader error|error CS|installed ->|BUNDLE NOT PRODUCED"

# A bundle under a megabyte means the shaders were baked unsupported: they declare
# #pragma require wavebasic/waveballot and get silently dropped unless the project's
# graphics API is D3D12. The bundle still loads; only shader.isSupported goes false.
echo "== size check =="
ssh -o BatchMode=yes "$HOST" '
  $b = "%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app\vdgs\vdgs-shaders"
  if (-not (Test-Path $b)) { "MISSING"; exit 1 }
  $n = (Get-Item $b).Length
  if ($n -lt 1000000) { "SUSPICIOUS: $n bytes - shaders were probably baked unsupported" }
  else { "ok: $n bytes" }
' 2>&1 | quiet
