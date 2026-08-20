#!/usr/bin/env bash
# Build the VDGS plugin and push it to the Windows box over SSH.
#
#   bash tools/deploy.sh              # plugin + every scene under build/splats/
#   bash tools/deploy.sh --plugin     # plugin only
#
# --plugin exists because the scenes are 2.2 GB and the DLL is 100 KB, so a one-line
# C# change was paying minutes of transfer to re-send splat data that had not
# changed. Resist the urge to write a second script for that - the launch path was
# once split in two and the copies drifted until each had a step the other lacked.
#
# The game path contains spaces and the remote default shell is PowerShell, which
# does not treat backslash as an escape. Quoting a spaced path through scp is a
# reliable way to lose the file, so we scp to a space-free staging path under the
# remote home and let a PowerShell Copy-Item put it in place.
set -euo pipefail

# Written as an if, not `[ ] && x`: under `set -e` that form's exit status is the
# test's, and whether bash then kills the script is subtle enough that someone would
# eventually "fix" it in the wrong direction.
PLUGIN_ONLY=0
if [ "${1:-}" = "--plugin" ]; then PLUGIN_ONLY=1; fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
. "$ROOT/tools/_remote.sh"

quiet() { grep -viE "post-quantum|store now|need to be upgraded|^\*\*" || true; }

# Optional override from tools/local.env, applied on the far side for this session.
REMOTE_GAME=""
if [ -n "${VDGS_GAME:-}" ]; then
  REMOTE_GAME="\$env:VDGS_GAME = '$(printf '%s' "$VDGS_GAME" | sed "s/'/''/g")'; "
fi

echo "== build =="
dotnet build "$ROOT/src/VDGS/VDGS.csproj" -c Release | tail -3

echo "== stage =="
ssh -o BatchMode=yes "$HOST" \
  "${REMOTE_GAME}New-Item -ItemType Directory -Force -Path (Join-Path \$env:USERPROFILE 'vdgs-stage') | Out-Null; Write-Output ok" \
  2>&1 | quiet
scp -o BatchMode=yes -q "$ROOT/src/VDGS/bin/Release/VDGS.dll" "$HOST:vdgs-stage/VDGS.dll" 2>&1 | quiet

# The shader bundle is built ON the Windows box (macOS cannot run DXC for D3D),
# so it is never staged from here - see tools/build-shaders-win.ps1.

# Splat scenes: build/splats/<name>/ -> <game>/vdgs/<name>/
if [ "$PLUGIN_ONLY" = 0 ] && [ -d "$ROOT/build/splats" ]; then
  for dir in "$ROOT/build/splats"/*/; do
    [ -d "$dir" ] || continue
    name="$(basename "$dir")"
    echo "== stage splat scene: $name =="
    ssh -o BatchMode=yes "$HOST" \
      "New-Item -ItemType Directory -Force -Path (Join-Path (Join-Path \$env:USERPROFILE 'vdgs-stage') 'splats\\$name') | Out-Null" \
      2>&1 | quiet
    scp -o BatchMode=yes -q "$dir"* "$HOST:vdgs-stage/splats/$name/" 2>&1 | quiet
  done
fi

echo "== install =="
ssh -o BatchMode=yes "$HOST" "
  ${REMOTE_GAME}\$PLUGIN_ONLY = $PLUGIN_ONLY
  \$home = \$env:USERPROFILE
  \$stage = Join-Path \$home 'vdgs-stage'
  \$game = if (\$env:VDGS_GAME) { \$env:VDGS_GAME } else { Join-Path \$home 'Downloads\\Velocidrone Windows Launcher\\app' }
  New-Item -ItemType Directory -Force -Path (Join-Path \$game 'BepInEx\\plugins') | Out-Null
  Copy-Item (Join-Path \$stage 'VDGS.dll') (Join-Path \$game 'BepInEx\\plugins\\VDGS.dll') -Force
  New-Item -ItemType Directory -Force -Path (Join-Path \$game 'vdgs') | Out-Null
  if (\$PLUGIN_ONLY -eq 0 -and (Test-Path (Join-Path \$stage 'splats'))) {
    foreach (\$d in Get-ChildItem (Join-Path \$stage 'splats') -Directory) {
      \$dst = Join-Path (Join-Path \$game 'vdgs') \$d.Name
      New-Item -ItemType Directory -Force -Path \$dst | Out-Null
      # placement.json is edited in-game (Web UI); never clobber an existing one, but do
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
  Get-ChildItem (Join-Path \$game 'BepInEx\\plugins') | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize
  Write-Output '-- vdgs dir --'
  Get-ChildItem (Join-Path \$game 'vdgs') -Recurse | Select-Object FullName,Length | Format-Table -AutoSize
" 2>&1 | quiet
