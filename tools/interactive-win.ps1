# Run any program on the Windows box's real desktop and leave it there.
#
#   pwsh -File interactive-win.ps1 -Name <task> -Exe <path> [-Args <string>] [-WorkDir <dir>]
#
# An SSH session is session 0: no window station, and Windows OpenSSH kills the whole
# process tree when the session ends, so Start-Process opens nothing a person can see
# and does not outlive the shell. A one-shot scheduled task registered against the
# logged-on user runs in their session, which is where the screen is. appstart-win.ps1
# and launch-win.ps1 are this with the exe fixed; this one takes it as a parameter, so a
# fresh box needs nothing placed by hand first.
#
# The principal is the current identity by name, not $env:USERDOMAIN\$env:USERNAME:
# USERDOMAIN is empty in an SSH session and the task then fails to register with
# "No mapping between account names and security IDs".
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Exe,
    [string]$Args = '',
    [string]$WorkDir = ''
)
$ErrorActionPreference = 'Stop'
if (-not $WorkDir) { $WorkDir = Split-Path $Exe }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = if ($Args) {
    New-ScheduledTaskAction -Execute $Exe -Argument $Args -WorkingDirectory $WorkDir
} else {
    New-ScheduledTaskAction -Execute $Exe -WorkingDirectory $WorkDir
}
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
# No time limit: the default stops the task (and the game) after 72 hours, and a
# session left flying overnight is exactly what perf logs are for.
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $Name -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $Name
Write-Output "started $Name"
