# Launch VelociDrone with the mod, in the desktop session, and leave it running.
#
# An SSH shell lives in session 0, which has no window station, so DirectX cannot create
# a swap chain and Unity dies partway through startup - before Mono finishes loading, so
# BepInEx's Chainloader never runs and it looks like the plugin is broken. A scheduled
# task with an Interactive principal runs in the logged-on desktop session instead.
#
# -UserId must come from (whoami), not "$env:USERDOMAIN\$env:USERNAME": USERDOMAIN is
# empty over SSH and the registration fails with "No mapping between account names and
# security IDs was done".
#
# Side effect: the game appears on the user's physical screen. Do not do this to someone
# mid-task without asking.
#
#   bash tools/launch-win.sh                  # ships this script and runs it
#   bash tools/launch-win.sh -Diagnose        # ...then dumps logs and stops the game
param([string]$GameArgs = '-force-d3d12', [switch]$Diagnose)

$ErrorActionPreference = 'Stop'
$app    = '%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app'
$exe    = Join-Path $app 'velocidrone.exe'
$probe  = Join-Path $app 'vdgs-probe.log'
$bepLog = Join-Path $app 'BepInEx\LogOutput.log'
$player = Join-Path $env:USERPROFILE 'AppData\LocalLow\velocidrone\velocidrone\Player.log'
$task   = 'VDGS-Launch'

foreach ($f in @($probe, $bepLog)) { if (Test-Path $f) { Remove-Item $f -Force } }

# Two copies fight over the same settings db, and the second one usually loses in a way
# that looks like a mod bug.
$old = Get-Process velocidrone -ErrorAction SilentlyContinue
if ($old) {
    foreach ($p in $old) { Write-Output ("stopping stale pid " + $p.Id); Stop-Process -Id $p.Id -Force }
    Start-Sleep -Seconds 3
}

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $app -Argument $GameArgs
$principal = New-ScheduledTaskPrincipal -UserId (whoami).Trim() -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                                         -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null

Write-Output "== launching in session 1: $GameArgs =="
Start-ScheduledTask -TaskName $task

$found = $null
$deadline = (Get-Date).AddSeconds(40)
while (-not $found -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $found = Get-Process velocidrone -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($found) { Write-Output ("pid " + $found.Id + " session " + $found.SessionId) }
else { Write-Output "process not found after 40s"; exit 1 }

if (-not $Diagnose) {
    Write-Output "game left running - control it at http://<host>:8777/"
    Write-Output "  (pass -Diagnose to dump logs and stop it instead)"
    exit 0
}

# --- diagnostic mode only, from here down ------------------------------------------
#
# This tail used to run unconditionally, and a Stop-Process -Force at the end of it looks
# exactly like the game crashing about forty seconds after launch: no crash dump, no
# Windows event, Player.log truncated mid-line, and the timing repeatable because it is a
# fixed sleep. That cost a long detour and two wrong diagnoses. It only runs when asked
# for now.
$deadline = (Get-Date).AddSeconds(100)
while (-not (Test-Path $bepLog) -and -not (Test-Path $probe) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 25

Write-Output "`n=== VDGS lines from the BepInEx log ==="
if (Test-Path $bepLog) {
    Get-Content $bepLog | Select-String -Pattern 'VDGS|Chainloader started' | Select-Object -First 20
} else { Write-Output "(absent - the Chainloader never ran)" }

Write-Output "`n=== splat spawn report ==="
if (Test-Path $probe) {
    Get-Content $probe | Select-String -Pattern "splats=|spawned|shaders READY|found splat|load failed|ply '|EXCEPTION" |
        Select-Object -First 20
} else { Write-Output "(absent)" }

# BepInEx.cfg has UnityLogListening = false, so Unity's own exceptions only reach
# Player.log - where PostProcessing's D3D12 complaint repeats until the file is tens of
# megabytes. Filter it or the real lines are unfindable.
Write-Output "`n=== Player.log, minus the PostProcessing spam ==="
if (Test-Path $player) {
    Get-Content $player -Tail 400 |
        Where-Object { $_ -notmatch 'KEyeHistogramClear|MultiScaleVODownsample|PostProcessing|^\s*at |^\s*$|ArgumentException' } |
        Select-Object -Last 25
} else { Write-Output "(absent)" }

Write-Output "`n=== stopping ==="
$proc = Get-Process velocidrone -ErrorAction SilentlyContinue
if ($proc) { foreach ($p in $proc) { Write-Output ("stopping " + $p.Id); Stop-Process -Id $p.Id -Force } }
else { Write-Output "already exited" }
