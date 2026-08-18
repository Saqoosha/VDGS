# Using VDGS

*[日本語版](USAGE.ja.md)*

How to install and operate the mod that renders 3D Gaussian Splatting captures inside
VelociDrone.

For internals, design decisions and the traps behind them, see
[ARCHITECTURE.md](ARCHITECTURE.md) and [AGENTS.md](../AGENTS.md). This file is procedure
only.

---

## 1. What you need

**To run the mod:**

| | |
|---|---|
| VelociDrone | a Unity 2021.3.45f2 build (verified on 1.16 and later) |
| GPU | **D3D12 capable**. DX11 will not work — see §3 |
| BepInEx | 5.4.23.5 win_x64 |

**To build the mod:**

| | |
|---|---|
| .NET SDK | to compile `src/VDGS` |
| Unity 2021.3.45f2 | to bake the shader AssetBundle. **Windows only** |
| Unity 2022.3.x | optional — only for the offline `.ply` converter |

The mod reads `.ply` files directly, so **converting a capture is optional**. Convert when
you want a smaller file on disk or a faster load.

---

## 2. Installing

### 2-1. BepInEx

Unpack [BepInEx 5.4.23.5 win_x64](https://github.com/BepInEx/BepInEx/releases) into the
game folder.

```powershell
$app = '<VelociDrone>\app'
Invoke-WebRequest 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip' -OutFile "$env:TEMP\bepinex.zip"
Expand-Archive "$env:TEMP\bepinex.zip" -DestinationPath $app -Force
```

Launch the game once and quit; that generates `BepInEx\config\BepInEx.cfg`.

**If you want logs**, append this (5.4.23 has disk logging off by default):

```ini
[Logging.Disk]
Enabled = true
LogLevel = Fatal, Error, Warning, Message, Info

[Logging]
UnityLogListening = false
```

`UnityLogListening = false` is close to mandatory. Under `-force-d3d12` the game's own
Auto Exposure throws every frame, and without this the log fills with it. It is harmless
otherwise.

### 2-2. The shader AssetBundle

**This can only be baked by Unity 2021.3.45f2 on Windows.** Unity on macOS refuses to run
DXC against D3D and emits empty shaders without raising an error.

```powershell
# open unity/VDGSBundler as a project and run two steps
Unity.exe -batchmode -quit -nographics -projectPath <VDGSBundler> `
          -executeMethod BuildBundles.SetGraphicsApis -logFile -
Unity.exe -batchmode -quit -nographics -projectPath <VDGSBundler> `
          -executeMethod BuildBundles.BuildWindows -vdgsOut <destination> -logFile -
```

Put the resulting `vdgs-shaders` at `<VelociDrone>\app\vdgs\vdgs-shaders`.

**Check that it is at least 1 MB.** Tens of kilobytes means it is empty — either the
graphics API was not set or it was baked on the wrong OS. The bundle still loads
perfectly; only `shader.isSupported` goes false, which is easy to miss.

From a Mac with SSH access, `bash tools/bake-shaders.sh` does the whole round trip and
checks the size for you.

### 2-3. The plugin

```bash
bash tools/deploy.sh          # build, ship over SSH, install
```

Without SSH: `dotnet build src/VDGS/VDGS.csproj -c Release` and copy `VDGS.dll` into
`<VelociDrone>\app\BepInEx\plugins\`.

---

## 3. Launching

**Always pass `-force-d3d12`.**

```
velocidrone.exe -force-d3d12
```

The compute shader that sorts splats needs Shader Model 6 wave intrinsics
(`WavePrefixSum` and friends, 41 uses). Those instructions do not exist in DX11, so
without the flag nothing is drawn at all.

`-force-vulkan` **does not work**: VelociDrone itself is not built for Vulkan, so the
game's own shaders are missing and you get no picture.

### Launching over SSH

An SSH shell runs in session 0 and has no window station. DirectX cannot create a swap
chain there, and Unity dies before it has even finished loading Mono. The launch has to be
handed to the interactive session through the task scheduler.

`tools/launch-win.ps1` does that:

```bash
bash tools/launch-win.sh          # ships the script and runs it
```

The game stays running. Add `-Diagnose` to collect the log and stop it afterwards.

Windows-side scripts in this repo:

| File | Purpose |
|---|---|
| `bash tools/launch-win.sh` | launch in the interactive session and leave it running |
| `tools/capture-win.ps1` | launch, screenshot, quit — a smoke test |
| `tools/build-shaders-win.ps1` | bake the shader bundle and install it |
| `bash tools/bench-win.sh` | measure frame time on the real GPU |

---

## 4. Adding a capture

### 4-1. The quick way: drop in a .ply

```
<VelociDrone>\app\vdgs\myscene.ply
```

That is the whole procedure. The mod parses the header for the splat count and shows the
file in the UI like any other scene; it is read and uploaded when you display it.

Measured load times (RTX 3060): 0.32 s for 415k splats, 1.6 s for 2.17M, 2.3 s for 3.18M.
Rendering lands about 7% behind the best offline format. See
[ply-loading.md](ply-loading.md).

Placement, if you need it, goes next to the file as `myscene.placement.json`.

### 4-2. The converted way: smaller on disk, faster to load

```bash
bash tools/reprocess.sh [scene]
```

or by hand:

```bash
Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
      -executeMethod PlyExporter.Run \
      -vdgsInput /abs/path/scene.ply \
      -vdgsOutput /abs/path/build/splats/<name> \
      -vdgsQuality High -logFile -
```

**Use `High`.** It is 84 bytes per splat, the fastest tier measured on the RTX 3060, and
the most faithful. `VeryHigh` is 236 B/splat for no visual gain, and `Medium` and below
render some captures far too dark. The reasoning is in
[performance.md](performance.md).

The output directory holds `meta.json` plus five binaries; copy it to
`<VelociDrone>\app\vdgs\<name>\`.

### 4-3. Orientation and scale

**This is the fiddly part with real data.** COLMAP-derived captures come out mirrored,
often upside down or tilted, and one unit is not any particular number of metres.

The mirror is unconditional and has one fix:

```bash
python3 tools/align_ply.py in.ply out.ply --mirror y
```

Reflecting Y corrects the flip and the handedness together. `--rotate 180,0,0` cannot —
it is a rotation, and a mirror is not. Full reasoning in [alignment.md](alignment.md).

For everything else, use [SuperSplat](https://superspl.at/editor):

1. drag the `.ply` into the browser
2. **click the circle on the view cube** to switch to orthographic (front, side). Without
   perspective you can actually see whether the floor is level
3. select in the Scene Manager and type rotations into the **TRANSFORM panel**
4. scale so the room is life-sized — 2.4 to 2.7 m floor to ceiling
5. export as `.ply`

If the file is too heavy to work with, decide the angle on a thinned preview and then
apply the same numbers at full resolution:

```bash
python3 tools/align_ply.py big.ply preview.ply --sample 150000
python3 tools/align_ply.py in.ply out.ply --rotate -12,0,3 --ceiling 2.6
```

`--ceiling` derives scale from the stated height and drops the floor to y=0. Rotations are
**applied to each gaussian's orientation as well**; rotating positions alone leaves every
splat tilted.

Things to know:

- **SuperSplat exports with Y inverted.** A capture standing upright in the editor comes
  out upside down. `align_ply.py` checks the density profile and warns
- **`PlyExporter` never changes orientation.** If it looks wrong, the data or the export is
  wrong
- **SuperSplat reorders points on export**, so you cannot recover the rotation by diffing
  before and after. Write the TRANSFORM numbers down
- **Automatic floor detection (`--up`) does not work** — it finds walls. See
  [alignment.md](alignment.md)
- **Do not crop.** Percentile cropping deletes the walls of any room shot from the inside.
  If debris is in the way, state a box with `--bounds`

### 4-4. Placement

`<VelociDrone>\app\vdgs\<name>\placement.json` (or `<name>.placement.json` beside a
`.ply`):

```json
{
    "position": [0.0, 0.0, 0.0],
    "rotation": [0.0, 0.0, 0.0],
    "scale": 1.0
}
```

**The mod has no alignment UI.** The capture is expected to arrive in correct coordinates
at a correct scale; `placement.json` exists as a hand-edited last resort. Getting the
coordinates right is the capture's job, not the mod's.

### 4-5. Binding to tracks

**Which capture appears is decided by track name.**

`<VelociDrone>\app\vdgs\bindings.json`:

```json
{
  "2026 Fusion Flight Festival - Presented by Neos": ["shibuya"],
  "Split-S": ["luigi", "bonsai"]
}
```

You can write it by hand, but doing it from the UI is faster (§5).

- **An unbound track shows nothing.** That is safer than showing the wrong capture
- one track may bind several captures
- binding is **per track, not per scenery**, because many tracks share one scenery

Without `<VelociDrone>\app\vdgs\autospawn` (an empty file), automatic display is off
entirely.

---

## 5. Operating it from a browser

Once the game is running the mod serves a control UI at **`http://<host>:8777/`**. Open it
from any machine, including over Tailscale — the intended setup is watching the game
through Parsec while driving it from a browser on another computer.

```
┌─ VDGS Control ──────────────────────────────────┐
│  Current track                                  │
│  2026 Fusion Flight Festival - Presented by Neos │
│  bound to shibuya                               │
├─────────────────────────────────────────────────┤
│  Splat scenes on this machine                   │
│  [shown]  shibuya   934,442 splats              │
│  [show ]  luigi      14,526 splats              │
│                                                 │
│  [Bind shown splat to this track]               │
│  [Unbind this track]  [Hide all]                │
├─────────────────────────────────────────────────┤
│  Bindings                                       │
│  <track name>  →  shibuya       [remove]        │
└─────────────────────────────────────────────────┘
```

**No game key is taken.** The track editor's arrow keys and F7 keep working. The UI
refreshes every 1.5 seconds, so "Current track" follows along when you change track
in-game.

### Binding a capture

1. load a track (flying or in the editor, either works)
2. press **show** on the capture you want
3. press **Bind shown splat to this track**
4. from then on that track loads that capture automatically

### Developer keys (kept)

| Key | Action |
|---|---|
| F9 | append environment info to `vdgs-probe.log` |
| F10 | dump the scene tree to `vdgs-hierarchy.txt` |
| F12 | dump the track-name search to `vdgs-track.txt` (needle in `vdgs/needle.txt`) |

F5, F6, F7 and F8 are **unused**. F7 is the track editor's save-scene and would collide.

### HTTP API

The same one the UI uses.

| | |
|---|---|
| `GET /api/status` | current track, what is shown, what is available, all bindings |
| `POST /api/load` | `{"splat":"name"}` — show only that capture |
| `POST /api/unload` | `{}` — hide everything |
| `POST /api/bind` | `{"splats":["name"]}` — bind to the current track |
| `POST /api/unbind` | `{}` for the current track, `{"track":"name"}` for any |

**Always send a body with a POST.** `HttpListener` rejects a POST with no `Content-Length`
as `411 Length Required` before the mod's handler ever sees it, so
`curl -X POST .../api/unload` fails and `curl -X POST .../api/unload -d '{}'` succeeds.

---

## 6. Files the mod writes

Directly under `<VelociDrone>\app\`:

| File | Contents |
|---|---|
| `vdgs-probe.log` | environment, shader status, spawn results |
| `vdgs-perf.log` | frame time every 5 s (fps / avg / worst / splat count) |
| `vdgs-track.log` | track detection, binding, show and hide history |
| `vdgs-hierarchy.txt` | scene tree, from F10 |
| `vdgs-track.txt` | track-name search, from F12 |
| `BepInEx\LogOutput.log` | BepInEx and plugin logs |

---

## 7. When it does not work

| Symptom | Cause and fix |
|---|---|
| nothing appears | `-force-d3d12` missing. Check `graphicsDeviceType` in `vdgs-probe.log` |
| `shaders NOT READY` | the bundle is empty. Under 1 MB means rebake (§2-2) |
| `shader.isSupported=false` | same. A D3D12 bundle baked on macOS is always like this |
| plugin never loads | check you did not launch over SSH (§3). No `BepInEx\config\` means the Chainloader never ran |
| scattered debris | a stale `chunk.bin` from a previous conversion — the deploy now deletes those, so re-deploy. Otherwise outliers in the source; bound them with `align_ply.py --bounds` |
| everything at the origin, in a blob | the opposite case: chunked data whose `chunk.bin` went missing |
| too small or too large | `scale` in `placement.json`. COLMAP scale is arbitrary |
| freezes the moment it appears | tens of MB going to the GPU at once. **Show it before you fly.** Measured 2.9 s |
| log fills with exceptions | `UnityLogListening = false` (§2-1). Harmless |

---

## 8. One caution

**Do not use this on leaderboards or in multiplayer.**

VelociDrone ships `ACTk.Runtime.dll` (Anti-Cheat Toolkit). Whether it is actually used for
detection is unverified, but submitting times from a modified client violates the terms.
Treat this as local flying only.

A PatchKit update wipes the plugin and the shaders. Re-run `tools/deploy.sh`. If BepInEx
itself is gone too, start again from §2-1.
