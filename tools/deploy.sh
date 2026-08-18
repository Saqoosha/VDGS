#!/usr/bin/env bash
# Build the VDGS plugin and push it to the Windows box over Tailscale SSH.
#
#   bash tools/deploy.sh              # plugin + every scene under build/splats/
#   bash tools/deploy.sh --plugin     # plugin only
#
# --plugin exists because the scenes are 2.2 GB and the DLL is 100 KB, so a one-line
# C# change was paying five minutes of Tailscale to re-send splat data that had not
# changed. Resist the urge to write a second script for that - the launch path was
# once split in two and the copies drifted until each had a step the other lacked.
#
# The game path contains spaces and the remote default shell is PowerShell, which
# does not treat backslash as an escape. Quoting a spaced path through scp is a
# reliable way to lose the file, so we scp to a space-free staging path and let a
# PowerShell Copy-Item put it in place.
set -euo pipefail

# Written as an if, not `[ ] && x`: under `set -e` that form's exit status is the
# test's, and whether bash then kills the script is subtle enough that someone would
# eventually "fix" it in the wrong direction.
PLUGIN_ONLY=0
if [ "${1:-}" = "--plugin" ]; then PLUGIN_ONLY=1; fi

HOST="${VDGS_HOST:-user@windows-box}"
GAME='%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app'
STAGE='%USERPROFILE%/vdgs-stage'
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

quiet() { grep -viE "post-quantum|store now|need to be upgraded|^\*\*" || true; }

echo "== build =="
dotnet build "$ROOT/src/VDGS/VDGS.csproj" -c Release | tail -3

echo "== stage =="
ssh -o BatchMode=yes "$HOST" "New-Item -ItemType Directory -Force -Path '$STAGE' | Out-Null; New-Item -ItemType Directory -Force -Path '$GAME\\BepInEx\\plugins' | Out-Null; Write-Output ok" 2>&1 | quiet
scp -o BatchMode=yes -q "$ROOT/src/VDGS/bin/Release/VDGS.dll" "$HOST:$STAGE/VDGS.dll" 2>&1 | quiet

# The shader bundle is built ON the Windows box (macOS cannot run DXC for D3D),
# so it is never staged from here - see tools/build-shaders-win.ps1.

# Splat scenes: build/splats/<name>/ -> <game>/vdgs/<name>/
if [ "$PLUGIN_ONLY" = 0 ] && [ -d "$ROOT/build/splats" ]; then
  for dir in "$ROOT/build/splats"/*/; do
    [ -d "$dir" ] || continue
    name="$(basename "$dir")"
    echo "== stage splat scene: $name =="
    ssh -o BatchMode=yes "$HOST" "New-Item -ItemType Directory -Force -Path '$STAGE\\splats\\$name' | Out-Null" 2>&1 | quiet
    scp -o BatchMode=yes -q "$dir"* "$HOST:$STAGE/splats/$name/" 2>&1 | quiet
  done
fi

echo "== install =="
ssh -o BatchMode=yes "$HOST" "
  \$PLUGIN_ONLY = $PLUGIN_ONLY
  Copy-Item '$STAGE\\VDGS.dll' '$GAME\\BepInEx\\plugins\\VDGS.dll' -Force
  New-Item -ItemType Directory -Force -Path '$GAME\\vdgs' | Out-Null
  if ($PLUGIN_ONLY -eq 0 -and (Test-Path '$STAGE\\splats')) {
    foreach (\$d in Get-ChildItem '$STAGE\\splats' -Directory) {
      \$dst = Join-Path '$GAME\\vdgs' \$d.Name
      New-Item -ItemType Directory -Force -Path \$dst | Out-Null
      # placement.json is edited in-game (F5); never clobber an existing one, but do
      # seed it the first time so a new scene lands somewhere sensible.
      Get-ChildItem \$d.FullName -File | ForEach-Object {
        \$target = Join-Path \$dst \$_.Name
        if (\$_.Name -eq 'placement.json' -and (Test-Path \$target)) {
          Write-Output ('  keeping in-game placement for ' + \$d.Name)
        } else {
          Copy-Item \$_.FullName \$target -Force
        }
      }
      # Copying alone is not enough: a scene converted at a different quality emits a
      # different set of files, and whatever the new set does not overwrite survives.
      # A leftover chunk.bin is the dangerous one - the shader applies chunk data on
      # presence alone, so an absolute-position scene gets lerped into scattered
      # debris with no error anywhere. Anything the source no longer has, goes.
      \$keep = @{}
      Get-ChildItem \$d.FullName -File | ForEach-Object { \$keep[\$_.Name] = \$true }
      Get-ChildItem \$dst -File | ForEach-Object {
        if (-not \$keep.ContainsKey(\$_.Name) -and \$_.Name -ne 'placement.json') {
          Write-Output ('  removing stale ' + \$d.Name + '/' + \$_.Name)
          Remove-Item \$_.FullName -Force
        }
      }
    }
  }
  Write-Output '-- plugins --'
  Get-ChildItem '$GAME\\BepInEx\\plugins' | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize
  Write-Output '-- vdgs dir --'
  Get-ChildItem '$GAME\\vdgs' -Recurse | Select-Object FullName,Length | Format-Table -AutoSize
" 2>&1 | quiet
