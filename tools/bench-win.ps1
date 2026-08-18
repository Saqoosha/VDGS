# Run RenderBench on the Windows box, against the GPU that actually flies the sim.
#
# The Mac numbers only give ratios; drjohnson has to be judged on the RTX 3060 under
# D3D12, because that is where the fan noise is.
#
# Two things make this awkward and both are load-bearing:
#
#   * An SSH shell lives in session 0, which has no window station, so a graphics-mode
#     Unity dies before it renders anything. The Editor has to be started from a
#     scheduled task with an Interactive principal, exactly like the game.
#   * The splat compute shader declares wave intrinsics and needs Shader Model 6, so the
#     Editor must run under D3D12. Without -force-d3d12 the shaders load but report
#     unsupported, and the bench would time an empty screen without saying so.
#
#   powershell -ExecutionPolicy Bypass -File bench-win.ps1 -Scenes bonsai,drjohnson
#
param(
    [string]$Scenes = 'playroom,bonsai,drjohnson,drjohnson-shc',
    [int]$Size = 1024,
    [int]$Frames = 120,
    [int]$SortNth = 1,
    [int]$Inside = 0,
    [int]$Cull = 1,
    [double]$CullMargin = 0.5,
    [string]$Tgz = '%USERPROFILE%\vdgs-bench.tgz',
    [string]$Project = '%USERPROFILE%\VDGSBench'
)

$ErrorActionPreference = 'Stop'
$game = '%USERPROFILE%\Downloads\Velocidrone Windows Launcher\app'
$task = 'VDGS-Bench'

$editor = '%USERPROFILE%\UnityEditors\2021.3.45f2\Editor\Unity.exe'
if (-not (Test-Path $editor)) { throw "Unity 2021.3.45f2 not found at $editor" }

if (Test-Path $Tgz) {
    Write-Output '== unpacking project =='
    if (Test-Path $Project) { Remove-Item $Project -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Project | Out-Null
    tar -xzf $Tgz -C $Project
}
if (-not (Test-Path (Join-Path $Project 'Assets'))) { throw "no Assets under $Project" }

function Invoke-Bench([string]$sceneDir, [string]$label) {
    $log = Join-Path $env:TEMP "vdgs-bench-$label.log"
    if (Test-Path $log) { Remove-Item $log -Force }

    $argv = "-batchmode -quit -force-d3d12 -projectPath `"$Project`" " +
            "-executeMethod RenderBench.Run -vdgsScene `"$sceneDir`" " +
            "-vdgsSize $Size -vdgsFrames $Frames -vdgsSortNth $SortNth " +
            "-vdgsInside $Inside -vdgsCull $Cull -vdgsCullMargin $CullMargin -logFile `"$log`""

    $action = New-ScheduledTaskAction -Execute $editor -Argument $argv -WorkingDirectory $Project
    $principal = New-ScheduledTaskPrincipal -UserId (whoami).Trim() -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false
    }
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $task

    # Unity can take a while to import on the first run; wait for the process to appear
    # and then for it to go away again.
    $deadline = (Get-Date).AddMinutes(20)
    while (-not (Get-Process Unity -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
    }
    while ((Get-Process Unity -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
    }

    if (Test-Path $log) {
        $line = Select-String -Path $log -Pattern '\[VDGS\] BENCH' | Select-Object -Last 1
        if ($line) { Write-Output $line.Line.Trim() }
        else {
            Write-Output "  $label : no BENCH line"
            Select-String -Path $log -Pattern 'error CS|Shader error|not supported|Exception' |
                Select-Object -First 5 | ForEach-Object { '    ' + $_.Line.Trim() }
        }
    } else {
        Write-Output "  $label : no log at $log"
    }
}

Write-Output "== graphics device =="
Invoke-Bench 'none' 'floor'

foreach ($s in $Scenes.Split(',')) {
    $dir = Join-Path $game "vdgs\$s"
    if (-not (Test-Path (Join-Path $dir 'meta.json'))) {
        Write-Output "  $s : not deployed"
        continue
    }
    Invoke-Bench $dir $s
}

if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
