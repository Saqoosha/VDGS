param([int]$WaitSeconds = 12)

# Screenshot a GUI that has to run where a desktop exists. An SSH shell is session 0 and
# has no window station, so the app is started through a scheduled task registered against
# the logged-on user, exactly as the game is.
$ErrorActionPreference = 'Stop'
$homeDir = $env:USERPROFILE
$exe  = Join-Path $homeDir 'VDGS\app\VDGS.exe'
$shot = Join-Path $homeDir 'VDGS\vdgs-appshot.png'
New-Item -ItemType Directory -Force -Path (Join-Path $homeDir 'VDGS') | Out-Null
$task = 'VDGS-AppShot'

if (Test-Path $shot) { Remove-Item $shot -Force }

$inner = @"
Start-Process -FilePath '$exe'
Start-Sleep -Seconds $WaitSeconds
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
`$b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
`$bmp = New-Object Drawing.Bitmap `$b.Width, `$b.Height
`$g = [Drawing.Graphics]::FromImage(`$bmp)
`$g.CopyFromScreen(`$b.Location, [Drawing.Point]::Empty, `$b.Size)
`$bmp.Save('$shot', [Drawing.Imaging.ImageFormat]::Png)
`$g.Dispose(); `$bmp.Dispose()
Get-Process VDGS -ErrorAction SilentlyContinue | Stop-Process -Force
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
$principal = New-ScheduledTaskPrincipal -UserId (whoami).Trim() -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal | Out-Null
Start-ScheduledTask -TaskName $task

$deadline = (Get-Date).AddSeconds($WaitSeconds + 60)
while (-not (Test-Path $shot) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if (Test-Path $shot) { Get-Item $shot | Select-Object Name, Length | Format-List }
else { Write-Output 'no screenshot - the app may have failed to start' }
