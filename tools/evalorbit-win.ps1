param(
    [int]$BootSeconds = 45,     # menu is up by then, measured - evalshot-win.ps1 uses the same
    [int]$LoadSeconds = 16,     # track + splats after the click
    [string]$Splat = '',        # capture to show through the mod's HTTP API; '' keeps whatever the track binds
    [string]$Splat2 = '',       # second capture: every pose is shot again with this one loaded
    [int]$SettleSeconds = 3,    # after moving the pinned camera: EvalCam re-reads its file once a second
    [string]$Poses = '',        # JSON array of { pos, fwd, up, fov }
    [string]$GameArgs = '-force-d3d12'
)

# Fly a fixed path through the real game with nobody watching, one screenshot per pose.
#
# evalshot-win.ps1 answers "what does this one view look like in the game"; this answers
# "what happens along a path", which is the question a level-of-detail switch lives in.
# One launch for the whole path, because the game costs 40 seconds to reach a track and a
# 17M-splat capture another 17 to upload.
#
# Everything happens inside the scheduled task: an SSH shell is session 0, which has no
# window station, so a game launched or a screen captured from there sees nothing.

$ErrorActionPreference = 'Stop'
$homeDir = $env:USERPROFILE
$app = if ($env:VDGS_GAME) { $env:VDGS_GAME } else { Join-Path $homeDir 'Downloads\Velocidrone Windows Launcher\app' }
$exe = Join-Path $app 'velocidrone.exe'
$evalcam = Join-Path $app 'vdgs\evalcam.json'
$outDir = Join-Path (Join-Path $homeDir 'VDGS') 'orbit'
$task = 'VDGS-EvalOrbit'

if (-not $Poses) { throw 'pass -Poses <json file>' }
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$inner = @"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class VdgsClick {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    public static void At(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(300);
        mouse_event(0x02, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
    }
}
'@
Start-Process -FilePath '$exe' -WorkingDirectory '$app' -ArgumentList '$GameArgs'
Start-Sleep -Seconds $BootSeconds

# QUICK START sits in the lower-left tile of the main menu, in fractions of the screen so
# a resolution change does not silently click empty space.
`$b = [Windows.Forms.Screen]::PrimaryScreen.Bounds

# Click, then ASK whether it worked: /api/status reports the loaded track's name, and it
# stays empty while the main menu is up. A click that lands a second early does nothing,
# and a whole unattended run then photographs the menu eight times.
`$onTrack = `$false
for (`$try = 0; `$try -lt 3 -and -not `$onTrack; `$try++) {
    [VdgsClick]::At([int](`$b.Width * 0.073), [int](`$b.Height * 0.815))
    `$deadline = (Get-Date).AddSeconds($LoadSeconds + 30)
    while ((Get-Date) -lt `$deadline) {
        Start-Sleep -Seconds 3
        try {
            `$s = Invoke-RestMethod -TimeoutSec 5 http://localhost:8777/api/status
            if (`$s.track) { `$onTrack = `$true; break }
        } catch { }
    }
}
if (-not `$onTrack) { 'no track' | Set-Content (Join-Path '$outDir' 'failed.txt') }
Start-Sleep -Seconds 3

# "CONTROLLER ERROR" greets a machine with no sticks plugged in and dims the whole frame.
# Its OK bar sits at the centre of the screen, 0.604 down - measured off a 4K capture.
[VdgsClick]::At([int](`$b.Width * 0.5), [int](`$b.Height * 0.604))
Start-Sleep -Seconds 2

if ('$Splat') {
    `$body = '{"splat":"$Splat"}'
    Invoke-RestMethod -Method Post -ContentType 'application/json' -Body `$body http://localhost:8777/api/load | Out-Null
    # The capture uploads tens of megabytes on the main thread; poll rather than guess.
    `$deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt `$deadline) {
        Start-Sleep -Seconds 3
        `$s = Invoke-RestMethod http://localhost:8777/api/status
        if (`$s.available | Where-Object { `$_.name -eq '$Splat' -and `$_.shown }) { break }
    }
}

`$poses = Get-Content '$Poses' -Raw | ConvertFrom-Json
`$i = 0
foreach (`$p in `$poses) {
    `$p | ConvertTo-Json -Compress | Set-Content -Encoding ASCII '$evalcam'
    Start-Sleep -Seconds $SettleSeconds
    `$bmp = New-Object Drawing.Bitmap `$b.Width, `$b.Height
    `$g = [Drawing.Graphics]::FromImage(`$bmp)
    `$g.CopyFromScreen(`$b.Location, [Drawing.Point]::Empty, `$b.Size)
    `$bmp.Save((Join-Path '$outDir' ('shot{0:000}.png' -f `$i)), [Drawing.Imaging.ImageFormat]::Png)
    `$g.Dispose(); `$bmp.Dispose()
    `$i++
}

if ('$Splat2') {
    `$body = '{"splat":"$Splat2"}'
    Invoke-RestMethod -Method Post -ContentType 'application/json' -Body `$body http://localhost:8777/api/load | Out-Null
    `$deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt `$deadline) {
        Start-Sleep -Seconds 3
        `$s = Invoke-RestMethod http://localhost:8777/api/status
        if (`$s.available | Where-Object { `$_.name -eq '$Splat2' -and `$_.shown }) { break }
    }
    `$i = 0
    foreach (`$p in `$poses) {
        `$p | ConvertTo-Json -Compress | Set-Content -Encoding ASCII '$evalcam'
        Start-Sleep -Seconds $SettleSeconds
        `$bmp = New-Object Drawing.Bitmap `$b.Width, `$b.Height
        `$g = [Drawing.Graphics]::FromImage(`$bmp)
        `$g.CopyFromScreen(`$b.Location, [Drawing.Point]::Empty, `$b.Size)
        `$bmp.Save((Join-Path '$outDir' ('alt{0:000}.png' -f `$i)), [Drawing.Imaging.ImageFormat]::Png)
        `$g.Dispose(); `$bmp.Dispose()
        `$i++
    }
}

# Leave the cameras back under the game's control, whatever happens next.
if (Test-Path '$evalcam') { Remove-Item '$evalcam' -Force }
Get-Process velocidrone -EA SilentlyContinue | ForEach-Object { Stop-Process -Id `$_.Id -Force }
'done' | Set-Content (Join-Path '$outDir' 'done.txt')
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
Start-ScheduledTask -TaskName $task

$poseCount = (Get-Content $Poses -Raw | ConvertFrom-Json).Count
$deadline = (Get-Date).AddSeconds($BootSeconds + $LoadSeconds + 180 + $poseCount * ($SettleSeconds + 4))
while (-not (Test-Path (Join-Path $outDir 'done.txt')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
Get-Process velocidrone -EA SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Write-Output ("shots: " + (Get-ChildItem $outDir -Filter 'shot*.png' | Measure-Object).Count + " of $poseCount")
