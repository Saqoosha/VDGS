param(
    [int]$BootSeconds = 45,     # menu is up by then
    [int]$LoadSeconds = 16,     # long enough for the track + splats, short enough that the idle drone has not drifted out of the map and triggered the game glitch overlay
    [string]$GameArgs = '-force-d3d12'
)

# Launch the game, click QUICK START, wait for the track, screenshot, quit.
#
# The whole point is that no human is in the loop: the evaluation camera is pinned by
# <game>/vdgs/evalcam.json, so the frame captured here is the same view a web viewer and
# the offline harness render, and the three can be subtracted.
#
# Everything runs inside one session-1 scheduled task. A click sent from the SSH shell
# (session 0) lands on no window station at all, exactly like a screenshot taken there
# comes back black.

$ErrorActionPreference = 'Stop'
$homeDir = $env:USERPROFILE
$app   = if ($env:VDGS_GAME) { $env:VDGS_GAME } else { Join-Path $homeDir 'Downloads\Velocidrone Windows Launcher\app' }
$exe   = Join-Path $app 'velocidrone.exe'
$probe = Join-Path $app 'vdgs-probe.log'
$shot  = Join-Path $homeDir 'vdgs-shot.png'
$task  = 'VDGS-EvalShot'

foreach ($f in @($shot)) { if (Test-Path $f) { Remove-Item $f -Force } }

# QUICK START sits in the lower-left tile of the main menu. Fractions of the screen
# rather than pixels, so a resolution change does not silently click empty space.
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
        mouse_event(0x02, 0, 0, 0, IntPtr.Zero);   // left down
        System.Threading.Thread.Sleep(60);
        mouse_event(0x04, 0, 0, 0, IntPtr.Zero);   // left up
    }
}
'@
Start-Process -FilePath '$exe' -WorkingDirectory '$app' -ArgumentList '$GameArgs'
Start-Sleep -Seconds $BootSeconds

`$b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
[VdgsClick]::At([int](`$b.Width * 0.073), [int](`$b.Height * 0.815))
Start-Sleep -Seconds $LoadSeconds

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

Write-Output "== launch + quick start + capture (boot ${BootSeconds}s, load ${LoadSeconds}s) =="
Start-ScheduledTask -TaskName $task

$deadline = (Get-Date).AddSeconds($BootSeconds + $LoadSeconds + 120)
while (-not (Test-Path $shot) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }

if (Test-Path $probe) {
    Write-Output "`n=== probe (evalcam / shaders) ==="
    Select-String -Path $probe -Pattern 'evalcam|shaders READY|supported=' |
        Select-Object -Last 6 | ForEach-Object { $_.Line }
}

Write-Output "`n=== screenshot ==="
if (Test-Path $shot) { Get-Item $shot | Select-Object Name, Length | Format-List }
else { Write-Output '(no screenshot - the click may have missed, or the track never loaded)' }

Write-Output "`n=== stopping ==="
$proc = Get-Process velocidrone -ErrorAction SilentlyContinue
if ($proc) { foreach ($p in $proc) { Stop-Process -Id $p.Id -Force }; Write-Output 'stopped' }
else { Write-Output 'already exited' }
