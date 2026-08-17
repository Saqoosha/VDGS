param([string]$GameArgs = '-force-d3d12')

$ErrorActionPreference = 'Stop'
$app    = '%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app'
$exe    = Join-Path $app 'velocidrone.exe'
$probe  = Join-Path $app 'vdgs-probe.log'
$bepLog = Join-Path $app 'BepInEx\LogOutput.log'
$task   = 'VDGS-Launch'

foreach ($f in @($probe, $bepLog)) { if (Test-Path $f) { Remove-Item $f -Force } }

# Kill any leftover instance first; two copies fight over the same settings db.
$old = Get-Process velocidrone -ErrorAction SilentlyContinue
if ($old) { foreach ($p in $old) { Write-Output ("stopping stale pid " + $p.Id); Stop-Process -Id $p.Id -Force }; Start-Sleep -Seconds 3 }

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $app -Argument $GameArgs
$principal = New-ScheduledTaskPrincipal -UserId (whoami).Trim() -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero)
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
else { Write-Output "process not found"; exit 1 }

# Give it long enough to reach the main menu and run the autospawn probe.
Start-Sleep -Seconds 55

Write-Output "`n=== VDGS lines from BepInEx log ==="
if (Test-Path $bepLog) {
    Get-Content $bepLog | Select-String -Pattern 'VDGS|Chainloader started' | Select-Object -First 12
} else { Write-Output "(no BepInEx log yet)" }

Write-Output "`n=== splat spawn report ==="
if (Test-Path $probe) {
    Get-Content $probe | Select-String -Pattern 'splats=|spawned|shaders READY|found splat|load failed|EXCEPTION' |
        Select-Object -First 15
} else { Write-Output "(no probe log yet)" }

Write-Output "`n== GAME LEFT RUNNING - it is yours now =="
Write-Output "   F8/Num0 toggle   Num4/6 X   Num8/2 Z   Num9/3 Y   Num7/1 yaw   Num+/- scale   F5 save"
