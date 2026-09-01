<#
.SYNOPSIS
    Installs VDGS (3D Gaussian Splatting for VelociDrone).

.DESCRIPTION
    Finds the VelociDrone install, fetches BepInEx if it is not already there,
    drops the plugin and shaders in place, and creates a desktop shortcut that
    launches the game with -force-d3d12.

    That last part matters more than it looks: the splat shaders need Shader
    Model 6 wave intrinsics, which DX11 does not have. Launched normally, the
    mod loads and renders nothing at all, with no error anywhere obvious.

.PARAMETER GamePath
    Path to the folder containing velocidrone.exe. Auto-detected when omitted.

.PARAMETER NoShortcut
    Skip creating the desktop shortcut.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -GamePath "D:\Games\Velocidrone\app"
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$BepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    $msg" -ForegroundColor Yellow }

# ---------------------------------------------------------------- find game

function Find-Game {
    if ($GamePath) {
        if (-not (Test-Path (Join-Path $GamePath 'velocidrone.exe'))) {
            throw "velocidrone.exe not found in: $GamePath"
        }
        return (Resolve-Path $GamePath).Path
    }

    # The PatchKit launcher installs under the user profile; Steam-style layouts
    # and manual copies are covered by the rest.
    $candidates = @(
        "$env:USERPROFILE\Downloads\Velocidrone Windows Launcher\app",
        "$env:LOCALAPPDATA\PatchKit\Apps",
        "$env:ProgramFiles\Velocidrone",
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Velocidrone"
    )

    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        if (Test-Path (Join-Path $c 'velocidrone.exe')) { return (Resolve-Path $c).Path }
        $found = Get-ChildItem $c -Recurse -Filter 'velocidrone.exe' -Depth 3 -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($found) { return $found.DirectoryName }
    }
    return $null
}

Write-Step 'Locating VelociDrone'
$game = Find-Game
if (-not $game) {
    Write-Host ''
    Write-Host 'Could not find velocidrone.exe automatically.' -ForegroundColor Red
    Write-Host 'Re-run with the folder that contains it, for example:' -ForegroundColor Red
    Write-Host '    .\install.ps1 -GamePath "C:\path\to\Velocidrone Windows Launcher\app"'
    exit 1
}
Write-Ok $game

# Refuse to touch a build the shaders cannot match: the bundle is version-locked.
$dataDir = Join-Path $game 'velocidrone_Data'
if (-not (Test-Path $dataDir)) { throw "velocidrone_Data not found next to the exe - is this the right folder?" }

# ---------------------------------------------------------------- BepInEx

Write-Step 'Checking BepInEx'
if (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.Preloader.dll')) {
    Write-Ok 'already installed'
} else {
    Write-Ok 'downloading BepInEx 5.4.23.5'
    $zip = Join-Path $env:TEMP 'vdgs-bepinex.zip'
    try {
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
    } catch {
        throw "BepInEx download failed: $($_.Exception.Message)`nDownload it manually from $BepInExUrl and extract into:`n  $game"
    }
    Expand-Archive -Path $zip -DestinationPath $game -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Write-Ok 'installed'
}

# ---------------------------------------------------------------- payload

Write-Step 'Installing VDGS'

$pluginSrc = Join-Path $here 'plugins\VDGS.dll'
$shaderSrc = Join-Path $here 'vdgs\vdgs-shaders'
foreach ($f in @($pluginSrc, $shaderSrc)) {
    if (-not (Test-Path $f)) { throw "missing from this package: $f" }
}

# A shader bundle built on macOS, or without the graphics API set, loads fine but
# every shader reports isSupported=false. Size is the cheapest way to catch that.
if ((Get-Item $shaderSrc).Length -lt 1MB) {
    throw "vdgs-shaders looks empty ($((Get-Item $shaderSrc).Length) bytes) - this package is broken"
}

$pluginDir = Join-Path $game 'BepInEx\plugins'
$vdgsDir   = Join-Path $game 'vdgs'
New-Item -ItemType Directory -Force -Path $pluginDir, $vdgsDir | Out-Null

Copy-Item $pluginSrc (Join-Path $pluginDir 'VDGS.dll') -Force
Copy-Item $shaderSrc (Join-Path $vdgsDir 'vdgs-shaders') -Force
Write-Ok "plugin  -> $pluginDir\VDGS.dll"
Write-Ok "shaders -> $vdgsDir\vdgs-shaders"

# ---------------------------------------------------------------- BepInEx cfg

Write-Step 'Configuring logging'
$cfg = Join-Path $game 'BepInEx\config\BepInEx.cfg'

# 5.4.23 ships with disk logging off, which makes every problem invisible. On a
# fresh install the file does not exist yet (BepInEx writes it on first launch),
# so write it ourselves - BepInEx merges in whatever else it needs rather than
# replacing the file. Without this branch, exactly the people who need logs most
# (first-time installers) are the ones who get none.
$logSettings = @'
[Logging.Disk]
Enabled = true
LogLevel = Fatal, Error, Warning, Message, Info

[Logging]
UnityLogListening = false
'@

if (Test-Path $cfg) {
    if ((Get-Content $cfg -Raw) -notmatch '\[Logging\.Disk\]') {
        Add-Content $cfg ("`r`n" + $logSettings)
        Write-Ok 'enabled disk logging'
    } else {
        Write-Ok 'already configured'
    }
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cfg) | Out-Null
    Set-Content -Path $cfg -Value $logSettings
    Write-Ok 'created BepInEx.cfg with disk logging enabled'
}

# ---------------------------------------------------------------- shortcut

if (-not $NoShortcut) {
    Write-Step 'Creating desktop shortcut'
    try {
        $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'VelociDrone (VDGS).lnk'
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($lnk)
        $sc.TargetPath = Join-Path $game 'velocidrone.exe'
        $sc.Arguments = '-force-d3d12'
        $sc.WorkingDirectory = $game
        $sc.Description = 'VelociDrone with 3D Gaussian Splatting (D3D12 required)'
        $sc.Save()
        Write-Ok $lnk
    } catch {
        Write-Warn2 "could not create shortcut: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- done

Write-Host ''
Write-Host 'VDGS installed.' -ForegroundColor Green
Write-Host ''
Write-Host '  1. Launch with the "VelociDrone (VDGS)" desktop shortcut.' -ForegroundColor White
Write-Host '     It passes -force-d3d12. Without it nothing will render.' -ForegroundColor Yellow
Write-Host '  2. Put converted captures in:' -ForegroundColor White
Write-Host "       $vdgsDir\<name>\   (meta.json + 5 .bin files)"
Write-Host '  3. Open the control panel while the game runs:' -ForegroundColor White
Write-Host '       http://localhost:8777/'
Write-Host '     Load a track, press "show" on a capture, then "Bind".' -ForegroundColor White
Write-Host ''
Write-Host '  Do not use on leaderboards or multiplayer.' -ForegroundColor Yellow
Write-Host ''
