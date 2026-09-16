# Screenshot the primary display, without touching any process. Run it on the desktop
# through interactive-win.ps1 - from an SSH session there is no screen to copy:
#
#   pwsh -File interactive-win.ps1 -Name shot -Exe "C:\Program Files\PowerShell\7\pwsh.exe" `
#        -Args "-NoProfile -WindowStyle Hidden -File <path>\shot-win.ps1"
#
# Writes %USERPROFILE%\VDGS\shot.png. appshot-win.ps1 is the older companion-specific
# form; it starts the app and stops it again, which is not what a running test wants.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$out = Join-Path $env:USERPROFILE 'VDGS\shot.png'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
