$ErrorActionPreference = 'Stop'
$homeDir    = $env:USERPROFILE
$tgz     = Join-Path $homeDir 'vdgs-bundler.tgz'
$root    = $homeDir
$project = Join-Path $homeDir 'VDGSBundler'
$outDir  = Join-Path $homeDir 'vdgs-bundles'
$game    = if ($env:VDGS_GAME) { $env:VDGS_GAME } else { Join-Path $homeDir 'Downloads\Velocidrone Windows Launcher\app' }

$editor = Join-Path $homeDir 'UnityEditors\2021.3.45f2\Editor\Unity.exe'
if (-not (Test-Path $editor)) {
    $found = Get-ChildItem (Join-Path $homeDir 'UnityEditors') -Recurse -Filter 'Unity.exe' -ErrorAction SilentlyContinue |
             Select-Object -First 1
    if (-not $found) { throw "Unity.exe not found under $(Join-Path $homeDir 'UnityEditors')" }
    $editor = $found.FullName
}
Write-Output "editor: $editor"

Write-Output "== unpacking project =="
if (Test-Path $project) { Remove-Item $project -Recurse -Force }
tar -xzf $tgz -C $root
if (-not (Test-Path $project)) { throw "unpack failed: $project missing" }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Invoke-Unity([string]$method, [string]$log, [string[]]$extra) {
    $argv = @(
        '-batchmode', '-quit', '-nographics',
        '-projectPath', $project,
        '-executeMethod', $method,
        '-logFile', $log
    ) + $extra

    $p = Start-Process -FilePath $editor -ArgumentList $argv -NoNewWindow -PassThru -Wait
    Write-Output ("  $method exit=" + $p.ExitCode)

    # Unity refuses to open a project a previous instance still holds. -Wait returns when
    # the process object exits, but the lock file can outlive it by a moment.
    $lock = Join-Path $project 'Temp\UnityLockfile'
    for ($i = 0; $i -lt 20 -and (Test-Path $lock); $i++) { Start-Sleep -Seconds 1 }
    if (Test-Path $lock) { Remove-Item $lock -Force -ErrorAction SilentlyContinue }
    while (Get-Process Unity -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
}

Write-Output "`n== step 1: graphics APIs -> D3D12 =="
$log1 = Join-Path $homeDir 'vdgs-shaderbuild-1.log'
Invoke-Unity 'BuildBundles.SetGraphicsApis' $log1 @()
Get-Content $log1 -EA SilentlyContinue | Select-String -Pattern '\[VDGS\]|error CS|Aborting' | Select-Object -First 8

Write-Output "`n== step 2: build shader bundle =="
$log2 = Join-Path $homeDir 'vdgs-shaderbuild-2.log'
Invoke-Unity 'BuildBundles.BuildWindows' $log2 @('-vdgsOut', $outDir)
Get-Content $log2 -EA SilentlyContinue |
    Select-String -Pattern '\[VDGS\]|Shader error|error CS|Aborting' | Select-Object -First 20

$bundle = Join-Path $outDir 'vdgs-shaders'
Write-Output "`n== result =="
if (Test-Path $bundle) {
    Get-Item $bundle | Select-Object Name, Length | Format-List
    New-Item -ItemType Directory -Force -Path "$game\vdgs" | Out-Null
    Copy-Item $bundle "$game\vdgs\vdgs-shaders" -Force
    Write-Output "installed -> $game\vdgs\vdgs-shaders"
} else {
    Write-Output "BUNDLE NOT PRODUCED at $bundle"
    if (Test-Path $outDir) { Get-ChildItem $outDir | Select-Object -ExpandProperty Name }
}
