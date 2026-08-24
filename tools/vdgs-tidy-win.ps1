# Move every capture no track points at out of the game, keeping it as a backup.
#
# Once a track ships with its capture, <game>/vdgs/ should hold exactly what some track
# names and nothing else - anything else is development leftovers that cost disk, slow the
# plugin's scan, and make the companion's list a place to hunt rather than read.
#
# What a track points at is bindings.json, so that file is the whole rule. Nothing is
# deleted: captures move to %USERPROFILE%\vdgs-dev, outside the game folder so a launcher
# update cannot take them with it.
#
#   powershell -File vdgs-tidy-win.ps1            # report only
#   powershell -File vdgs-tidy-win.ps1 -Apply     # move them
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$game = if ($env:VDGS_GAME) { $env:VDGS_GAME }
        else { Join-Path $env:USERPROFILE 'Downloads\Velocidrone Windows Launcher\app' }
$vdgs = Join-Path $game 'vdgs'
$dest = Join-Path $env:USERPROFILE 'vdgs-dev'

if (-not (Test-Path $vdgs)) { throw "no vdgs folder: $vdgs" }

# Empty bindings would make every capture look unused, which is the one case where this
# script must do nothing rather than move everything.
$bindingsPath = Join-Path $vdgs 'bindings.json'
if (-not (Test-Path $bindingsPath)) { throw "no bindings.json - refusing to guess" }
$bindings = Get-Content $bindingsPath -Raw | ConvertFrom-Json
$kept = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($p in $bindings.PSObject.Properties) {
    foreach ($name in $p.Value) { [void]$kept.Add($name) }
}
if ($kept.Count -eq 0) { throw "bindings.json names no captures - refusing to move everything" }

# A capture is a directory holding meta.json, or a bare .ply. Everything else in vdgs/ -
# the shader bundle, the built UI, bindings.json, the autospawn flag - belongs to the mod.
$moves = @()
foreach ($d in Get-ChildItem $vdgs -Directory) {
    if (-not (Test-Path (Join-Path $d.FullName 'meta.json'))) { continue }
    if ($kept.Contains($d.Name)) { continue }
    $moves += [pscustomobject]@{ Name = $d.Name; Paths = @($d.FullName) }
}
foreach ($f in Get-ChildItem $vdgs -Filter *.ply -File) {
    $name = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    if ($kept.Contains($name)) { continue }
    # The collision mesh and the placement live beside a .ply, not inside it.
    $side = @(Get-ChildItem $vdgs -File | Where-Object {
        $_.Name -eq "$name.ply" -or $_.Name -eq "$name.collision.bin" -or $_.Name -eq "$name.placement.json"
    })
    $moves += [pscustomobject]@{ Name = $name; Paths = @($side | ForEach-Object { $_.FullName }) }
}

'kept (a track names these):'
foreach ($k in $kept) { '  ' + $k }
''
if (-not $moves) { 'nothing to move'; return }

$total = 0
'to move:'
foreach ($m in $moves) {
    $size = ($m.Paths | ForEach-Object {
        if (Test-Path $_ -PathType Container) { (Get-ChildItem $_ -File -Recurse | Measure-Object Length -Sum).Sum }
        else { (Get-Item $_).Length }
    } | Measure-Object -Sum).Sum
    $total += $size
    '  {0,-24} {1,8:N1} MB  ({2} item(s))' -f $m.Name, ($size/1MB), $m.Paths.Count
}
'  {0,-24} {1,8:N1} MB  total' -f '', ($total/1MB)
''

if (-not $Apply) { 'report only - pass -Apply to move'; return }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($m in $moves) {
    foreach ($p in $m.Paths) {
        $target = Join-Path $dest (Split-Path $p -Leaf)
        if (Test-Path $target) { throw "already in the backup: $target" }
        Move-Item $p $target
    }
    '  moved ' + $m.Name
}
''
'backup: ' + $dest
'{0:N1} GB free on C:' -f ((Get-PSDrive C).Free/1GB)
