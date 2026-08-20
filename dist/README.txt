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

A .ply dropped in is enough for the picture:

    <game>\vdgs\myscene.ply

Converted directories work too:

    <game>\vdgs\<name>\
        meta.json
        chunk.bin  pos.bin  other.bin  color.bin  sh.bin

They appear in the control panel automatically.

Walls and floors need a collision mesh next to the capture:

    <game>\vdgs\myscene.collision.bin     (beside a .ply)
    <game>\vdgs\<name>\collision.bin      (inside a converted directory)

How to bake that mesh, and why indoor rooms must not be cropped, is in the
project repo: docs/SCENES.md.

While the game is running, open:

    http://localhost:8777/

  - Load a track in the game (flying or in the track editor)
  - Press "show" next to a capture
  - Press "Bind shown splat to this track"

From then on, loading that track shows that capture automatically.
Tracks with no binding show nothing.

Scale and height are on the control panel and write placement.json.
Rotation belongs in the capture before it arrives.

The control panel also works from another machine on your network:

    http://<this-pc>:8777/

which is handy if you stream the game with Parsec or similar - watch on one
screen, drive the mod from a browser on another.

The mod does not take any keyboard input. The track editor's arrow keys and
F7 (save scene) keep working normally.


MAKING CAPTURES
---------------

Drop a .ply into <game>\vdgs\ and it loads at runtime. Converting to the
smaller on-disk format is optional and needs Unity; see the project repo.

Two things worth knowing:

  - Do not crop indoor rooms. The walls are the outer shell, and percentile
    cropping deletes them. Cut giant unconstrained gaussians by size
    (--max-sigma), not by position. See docs/SCENES.md and docs/alignment.md.
  - COLMAP-derived captures have arbitrary scale. Get rotation right in
    SuperSplat; set scale and height on the Web UI.


IF NOTHING SHOWS UP
-------------------

  Nothing at all           You forgot -force-d3d12. Use the shortcut.
  "shaders NOT READY"      Reinstall; vdgs-shaders is missing or truncated.
  Debris everywhere        Giant unconstrained gaussians, or a leftover
                           chunk.bin. See docs/SCENES.md.
  Wrong size               Scale on the Web UI, or fix it where the capture
                           was produced.
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
