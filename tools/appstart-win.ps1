# Start the companion on the game box's real desktop and leave it there.
#
# An SSH session is session 0 and has no window station, so Start-Process from here opens
# nothing a person can see. A scheduled task registered against the logged-on user runs in
# their session, which is where the screen is.
$ErrorActionPreference = 'Stop'
$homeDir = $env:USERPROFILE
$exe  = Join-Path $homeDir 'vdgs-app\VDGS.exe'
$task = 'VDGS-AppStart'

if (-not (Test-Path $exe)) { throw "not installed: $exe" }

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$principal = New-ScheduledTaskPrincipal -UserId (whoami).Trim() -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal | Out-Null
Start-ScheduledTask -TaskName $task

# The task starting is not the app appearing; report the process, not the request.
for ($i = 0; $i -lt 20 -and -not (Get-Process VDGS -ErrorAction SilentlyContinue); $i++) {
    Start-Sleep -Milliseconds 500
}
$p = Get-Process VDGS -ErrorAction SilentlyContinue
if ($p) { "running: pid $($p.Id)" } else { 'the app did not start' }

# A shortcut, so starting it never needs this machine to be reachable over SSH again.
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'VDGS Companion.lnk'
if (-not (Test-Path $lnk)) {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = Split-Path $exe
    $s.Description = 'Install captures and start VelociDrone with the mod'
    $s.Save()
    "desktop shortcut: $lnk"
}
