VDGS - 3D Gaussian Splatting for VelociDrone
============================================

Displays photorealistic 3D Gaussian Splatting captures inside VelociDrone,
so you can fly a real, scanned location in the sim.


INSTALL
-------

1. Right-click install.ps1 -> "Run with PowerShell"

   Or from a PowerShell window:

       cd <this folder>
       powershell -ExecutionPolicy Bypass -File .\install.ps1

   If the game is not found automatically:

       .\install.ps1 -GamePath "C:\path\to\Velocidrone Windows Launcher\app"

2. Launch the game with the "VelociDrone (VDGS)" shortcut the installer put on
   your desktop.

   *** The shortcut passes -force-d3d12. This is not optional. ***

   The splat renderer sorts on the GPU using Shader Model 6 wave intrinsics,
   which DirectX 11 does not have. Started normally, the mod loads correctly
   and draws nothing, with no obvious error.


REQUIREMENTS
------------

  - VelociDrone built on Unity 2021.3.45f2 (1.16 or later)
  - A GPU that supports Direct3D 12
  - Windows x64

You do NOT need Unity. The shaders in this package are prebuilt.


USING IT
--------

Captures go in:

    <game>\vdgs\<name>\
        meta.json
        chunk.bin  pos.bin  other.bin  color.bin  sh.bin

They appear in the control panel automatically.

While the game is running, open:

    http://localhost:8777/

  - Load a track in the game (flying or in the track editor)
  - Press "show" next to a capture
  - Press "Bind shown splat to this track"

From then on, loading that track shows that capture automatically.
Tracks with no binding show nothing.

The control panel also works from another machine on your network:

    http://<this-pc>:8777/

which is handy if you stream the game with Parsec or similar - watch on one
screen, drive the mod from a browser on another.

The mod does not take any keyboard input. The track editor's arrow keys and
F7 (save scene) keep working normally.


MAKING CAPTURES
---------------

This package does not convert .ply files - that needs Unity. See the project
repository for the converter and the crop tool.

Two things worth knowing:

  - Every 3DGS reconstruction leaves a halo of junk gaussians ("floaters")
    around the subject. In a flight sim they read as debris hanging in the air.
    Crop them before converting.
  - COLMAP-derived captures have arbitrary scale. Get the scale and origin
    right in the tool that produced the capture; the mod does not move,
    rotate or scale anything.


IF NOTHING SHOWS UP
-------------------

  Nothing at all           You forgot -force-d3d12. Use the shortcut.
  "shaders NOT READY"      Reinstall; vdgs-shaders is missing or truncated.
  Debris everywhere        Floaters in the source data. Crop them.
  Wrong size               Fix the scale where the capture was produced.
  Freeze when showing      Tens of MB uploading to the GPU. Show it before
                           you start flying, not mid-flight.

Logs are written next to velocidrone.exe:

    vdgs-probe.log   environment, shader state, spawn results
    vdgs-track.log   track detection and bindings
    vdgs-perf.log    frame times


IMPORTANT
---------

Do not use this on leaderboards or in multiplayer. VelociDrone ships an
anti-cheat toolkit, and submitting times from a modified client is against
its terms. Local flying only.


UNINSTALL
---------

Delete from the game folder:

    BepInEx\plugins\VDGS.dll
    vdgs\

Removing BepInEx itself (winhttp.dll, doorstop_config.ini, BepInEx\) returns
the game to stock.


CREDITS
-------

Splat rendering ported from aras-p/UnityGaussianSplatting (MIT).
GPU sorting from b0nes164/GPUSorting (MIT, Thomas Smith).
See THIRD-PARTY-NOTICES.md.
