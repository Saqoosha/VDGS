param([int]$WaitSeconds = 90, [string]$GameArgs = '-force-d3d12')

$ErrorActionPreference = 'Stop'
$homeDir   = $env:USERPROFILE
$app    = if ($env:VDGS_GAME) { $env:VDGS_GAME } else { Join-Path $homeDir 'Downloads\Velocidrone Windows Launcher\app' }
$exe    = Join-Path $app 'velocidrone.exe'
$probe  = Join-Path $app 'vdgs-probe.log'
$bepLog = Join-Path $app 'BepInEx\LogOutput.log'
$shot   = Join-Path $homeDir 'vdgs-shot.png'
$task   = 'VDGS-Capture'

foreach ($f in @($probe, $bepLog, $shot)) { if (Test-Path $f) { Remove-Item $f -Force } }

# Everything - launching the game AND grabbing the screen - has to happen in the
# logged-on desktop session. Session 0 has no window station, so a screenshot taken
# from the SSH shell would be black even if the game were running fine.
$inner = @"
`$app = '$app'
Start-Process -FilePath '$exe' -WorkingDirectory `$app -ArgumentList '$GameArgs'
Start-Sleep -Seconds $WaitSeconds
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
`$b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
`$bmp = New-Object Drawing.Bitmap `$b.Width, `$b.Height
`$g = [Drawing.Graphics]::FromImage(`$bmp)
`$g.CopyFromScreen(`$b.Location, [Drawing.Point]::Empty, `$b.Size)
`$bmp.Save('$shot', [Drawing.Imaging.ImageFormat]::Png)
`$g.Dispose(); `$bmp.Dispose()
"@

$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
$principal = New-ScheduledTaskPrincipal -UserId (whoami).Trim() -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null

Write-Output "== launching + capturing in session 1 ($GameArgs, wait ${WaitSeconds}s) =="
Start-ScheduledTask -TaskName $task

$deadline = (Get-Date).AddSeconds($WaitSeconds + 90)
while (-not (Test-Path $shot) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }

function Show($path, $title, $lines) {
    Write-Output "`n=== $title ==="
    if (Test-Path $path) { Get-Content $path -TotalCount $lines } else { Write-Output "(absent)" }
}

Show $bepLog 'BepInEx log' 40
Show $probe 'probe log' 70

Write-Output "`n=== screenshot ==="
if (Test-Path $shot) { Get-Item $shot | Select-Object Name, Length | Format-List }
else { Write-Output "(no screenshot)" }

Write-Output "`n=== stopping ==="
$proc = Get-Process velocidrone -ErrorAction SilentlyContinue
if ($proc) { foreach ($p in $proc) { Write-Output ("stopping " + $p.Id); Stop-Process -Id $p.Id -Force } }
else { Write-Output "already exited" }
