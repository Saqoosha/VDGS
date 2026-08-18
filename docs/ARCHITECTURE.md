# How VDGS is built

*[日本語版](ARCHITECTURE.ja.md)*

This file is about **why it is the way it is**. Procedure lives in [USAGE.md](USAGE.md);
environment-specific measurements and the traps behind them in [AGENTS.md](../AGENTS.md).

---

## The shape of it

```
  Mac (development)                   Windows (execution)
┌──────────────────────┐          ┌────────────────────────────────┐
│ .ply / .spz          │          │  VelociDrone (Unity 2021.3.45f2)│
│   │                  │          │  ┌──────────────────────────┐  │
│   │ verify_orient.py │          │  │ BepInEx 5.4 (Doorstop)   │  │
│   ▼                  │          │  │   └─ VDGS.dll            │  │
│ PlyExporter          │          │  │        ├ PlyLoader       │  │
│ (Unity 2022.3)       │          │  │        ├ SplatRenderer   │  │
│   │  (optional)      │          │  │        ├ TrackName       │  │
│   ▼                  │ deploy.sh│  │        ├ TrackBindings   │  │
│ meta.json + 5 .bin ──┼─────────▶│  │        └ WebControl :8777│  │
│ or the .ply itself   │   (scp)  │  └──────────────────────────┘  │
│                      │          │            ▲                    │
│ VDGSBundler          │          │            │ HTTP               │
│ (Unity 2021.3.45f2)  │          └────────────┼────────────────────┘
│   │ bake on Windows  │                       │
│   ▼                  │                a browser (from the Mac, over Tailscale)
│ vdgs-shaders ────────┼──────────────────────┘
└──────────────────────┘
```

Two Unity projects, deliberately. **The versions cannot be the same:**

| | Unity | Why |
|---|---|---|
| `unity/VDGSBundler` | **2021.3.45f2** | shaders only load into the exact version the game was built with |
| `unity/VDGSConverter` | **2022.3.x** | UnityGaussianSplatting needs `com.unity.collections` 2.x, which will not install into 2021.3 |

The converter's output is plain binary, so its version does not matter to anything
downstream. Only the bundler's is strict.

---

## Why an AssetBundle is not enough

Objects the track editor can place come from ordinary AssetBundles (`trees`, `gates`,
`barriers`…). **Splats cannot simply be added to that set.** Three reasons:

1. **The MonoBehaviour types do not exist in the game.** A component inside a bundle is
   restored only if a class of the same name exists in the game's assemblies.
   `SplatRenderer` does not, so any prefab carrying it comes back with broken references
2. **Nothing would dispatch the compute shaders.** Sorting runs every frame, and some C#
   has to drive it
3. **A CommandBuffer must be inserted into the camera.** Rendering is not one material; it
   interrupts the camera's pipeline

Hence code injection through BepInEx. The AssetBundle is used **only as a container for
shaders**.

---

## The data

### Why the ScriptableObject was dropped

Upstream (aras-p/UnityGaussianSplatting) stores everything in a `GaussianSplatAsset`
ScriptableObject — which is nothing but **metadata plus five raw binary TextAssets**.

Inside an injected assembly that does not work:

- `AssetDatabase` does not exist; it is Editor-only
- restoring a ScriptableObject from a bundle requires the game to resolve its type, and
  `GaussianSplatAsset` is not in the game

**Putting the same bytes on disk as plain files makes the problem disappear.**

```
<game>/vdgs/<name>/
  meta.json     splat count, formats, bounds
  chunk.bin     ChunkInfo[] (64 bytes each). Optional
  pos.bin       positions. GraphicsBuffer.Target.Raw
  other.bin     rotation and scale. Same
  color.bin     colour. Uploaded as a Texture2D
  sh.bin        spherical harmonics. Same
```

`SplatData.Load()` reads it; `SplatRenderer` pushes it into GPU buffers.

### Or just a .ply

`<game>/vdgs/<name>.ply` works as well. `PlyLoader` parses it at load time and produces
exactly the buffers `SplatData` would have held, through `SplatData.FromBuffers`.

The offline pipeline was the harder half of the workflow to distribute: it wants a second
Unity install, a Python script and an SSH round trip. Reading the `.ply` directly removes
all of it. The cost is 7% of frame time and a slightly larger footprint (132 B/splat);
details and the three silent traps in the parser are in [ply-loading.md](ply-loading.md).

### `ChunkInfo` is 64 bytes

```
uint   colR, colG, colB, colA     4 x 4 = 16
float2 posX, posY, posZ           3 x 8 = 24
uint   sclX, sclY, sclZ           3 x 4 = 12
uint   shR,  shG,  shB            3 x 4 = 12
                                        = 64
```

This has to match the HLSL exactly, and **getting it wrong raises no exception — the
rendering just quietly breaks**. Confirmed against a real conversion (640 splats → 3
chunks → 192 bytes).

### chunk.bin is dangerous in both directions

The shader decides whether to decode chunk-relative values **purely from whether a chunk
buffer is bound**. So:

- a **leftover** `chunk.bin` applied to absolute positions extrapolates the capture into
  space and raises scale to the eighth power — the "scattered debris" that cost a day
- a **missing** one leaves every splat at its 0..1 weight, collapsing the capture into a
  blob at the origin

**`posFormat` does not settle it.** `Float32` is a storage width, not a coordinate space;
chunked scenes store 0..1 weights in Float32 quite happily. A first attempt at guarding
this inferred otherwise and broke every chunked capture. Only the conversion knows, so
`PlyExporter` writes `chunkCount` into `meta.json` and `SplatData.AcceptChunks` compares
against it. `deploy.sh` also deletes files that no longer exist in the source, which is
what let the stale file survive in the first place.

---

## Rendering

`SplatRenderSystem` and `SplatRenderer` are ports of upstream with these removed:

- editing (selection, cutouts, export)
- the URP and HDRP paths (VelociDrone is Built-in RP)
- dependencies on `Unity.Mathematics`, `Unity.Collections` and `Burst`
- `Unity.Profiling`

What is left depends on UnityEngine alone. `GpuSorting.cs` already did, so it is unchanged
apart from its namespace.

### A frame

```
Camera.onPreCull
  └ GatherSplatsForCamera      collect visible captures, back to front
  └ build the CommandBuffer
       ├ GetTemporaryRT        a dedicated RT (R16G16B16A16_SFloat)
       ├ CalcDistances         camera distance per splat, plus frustum culling
       ├ SortPoints            GPU radix sort
       ├ PrepareDrawArgs       visible count into the indirect args buffer
       ├ CalcViewData          screen-space data per splat, in compute
       ├ DrawProceduralIndirect  one quad per visible splat, instanced
       └ Composite             blend the RT onto the camera target
  └ inserted at CameraEvent.BeforeForwardAlpha
```

`BeforeForwardAlpha` puts it **after opaque geometry and before transparents**, which is
why depth against gates and the aircraft comes out right.

### Culling shares the sort

Frustum culling lives in the distance pass rather than in a separate compaction pass,
because that pass already decides draw order. A culled splat is given the maximum sort key
and lands past everything visible; the visible count is accumulated **one atomic per
wave** and fed to `DrawProceduralIndirect`. Worth 10.7% at zero cost to the image — the
derivation, and the two errors in it, are in [performance.md](performance.md).

### Why D3D12 is required

`DeviceRadixSort.hlsl` uses Shader Model 6 wave intrinsics (`WavePrefixSum`,
`WaveReadLaneAt`, 41 uses). DX11 has no such instructions.

`-force-vulkan` fails for a different reason: **the game itself** cannot render, because
VelociDrone is not built for Vulkan. So D3D12 is the only option.

### The backdrop

`SplatBackdrop` puts a black, inward-facing box around a capture so the game's skybox does
not show through the gaps. Two details worth knowing:

- **every face points inward**, and `AssertFacesInward` measures each normal against the
  centre rather than trusting the winding. The first version had all twelve triangles
  backwards
- **the floor sits at y = 0.01 in world space**, not at the box's own bottom, because the
  game's ground plane is at 0 and would otherwise hide it. It is pinned through
  `parent.InverseTransformPoint` so it stays there under any placement

---

## Tracks and captures

### Track name decides what is shown

Not scenery. **One scenery carries many tracks**, so keying on the scene would put the
capture on every track that uses that map.

```
bindings.json:  { "<track name>": ["<capture name>", ...] }
```

An unbound track shows nothing — safer than showing the wrong capture.

### Why it polls

`SceneManager.sceneLoaded` is not enough: the in-game change-track dialog **swaps the
track without changing the Unity scene**. So the track name is read once a second and the
captures are swapped when it changes (`PollTrack`).

### Finding the track name took brute force

Assembly-CSharp is obfuscated — field names look like `glnoaiifnln` — and **the string
constants are shuffled**, so decompiling produces confident lies.

`TrackProbe` (F12) walks every live object, UI text, property and collection and reports
which fields contain a given string. Name a track something unique like `VDGSPROBE7777`
and it falls out immediately.

The carriers found:

| Where | Behaviour |
|---|---|
| `InGameChangeTrack.glnoaiifnln` | follows the loading track. **First choice** |
| a `Track Name` label under `Current Track/Table Entry` | the only UI element that claims to be the current track |
| `RaceInfo2/View - Gameplay/TrackName` (TMP) | the flight HUD. Correct while flying; **keeps the last flown name after returning to the editor**, so it goes last |

Three things not to use:

- **`EditorManager.nnpnlmbjocf`** — "the track last opened *in the editor*". It is found
  first and looks perfect, and it does not update when you fly a different one. In an
  obfuscated build, **one value looking right once is not evidence**
- **`Tracks Admin Entry(Clone)/TrackEntry/Track` labels** — every track the user owns, one
  per row. Not the current one
- **a `Track Name` label matched by name alone** — the same modal also has a *column
  header* whose text is literally `Track Name`. Match on the path

`TrackName.cs` tries the carriers in order and falls back to scanning every string field
in the class, so an update that renames obfuscated fields does not necessarily break it.

---

## Control

### Why a browser

In-game keys were tried, and **all of them were taken**:

| Key | Conflict |
|---|---|
| F7 | the track editor's save-scene |
| arrow keys | the track editor's object movement |
| numpad | absent on a laptop |

Worse, **there is nowhere in the game to draw a HUD**, so pressing a key gave no feedback
at all until you read a log.

Moving out of the process removes all of it, and adds control from another machine
(watching through Parsec, driving from the Mac's browser).

```
HttpListener (:8777)
  ├ GET  /            embedded HTML (WebUi.cs)
  ├ GET  /api/status  current track / shown / available / all bindings
  ├ POST /api/load    show one capture
  ├ POST /api/unload  hide everything
  ├ POST /api/bind    bind to the current track
  └ POST /api/unbind  remove a binding
```

### The thread boundary

`HttpListener` runs on its own thread, and **Unity objects may only be touched from the
main thread**. Requests are pushed onto a `Queue<Action>` and drained by `Pump()` from
`Update()`.

`GET /api/status` has to return a value, so it posts to the main thread and waits on a
`ManualResetEventSlim` with a 5-second timeout — otherwise a stalled game would hang the
HTTP thread.

### Security

**A track name is attacker-controlled text.** VelociDrone downloads community tracks and
their names go straight into the UI, and the server is open to the whole LAN.

Three defences:

1. **No `innerHTML`.** Everything is `createElement` plus `textContent`. Skipping this
   once left a state where downloading a single track named `<img src=x onerror=...>` ran
   arbitrary code
2. **No CORS header.** The UI is served from the same origin and does not need one. Adding
   it would let any site the user visits drive this API
3. **POST requires `Content-Type: application/json`.** A cross-origin page cannot set that
   header without a preflight, and there is no CORS policy to satisfy it. That is the CSRF
   barrier — 2 alone is escapable with a `text/plain` simple request

---

## Why there is no alignment UI

There was one — move, rotate and scale on keys, saved to `placement.json`. **It was
deleted.**

Aligning splat coordinates inside the simulator is the wrong tool for the job:

- there is nowhere in the game to display a number, so it is all done by eye
- the capture and training side (Postshot and friends) can emit correct coordinates in the
  first place
- keeping it in the mod means redoing the alignment every time a capture is replaced

`placement.json` remains as a **hand-edited last resort**; nothing in-game writes it.

**Collision is absent for the same reason.** If a course needs something to fly through,
VelociDrone's own gates and barriers already have colliders.

---

## What each file does

| File | Responsibility |
|---|---|
| `Plugin.cs` | entry point, wiring, track polling |
| `SplatData.cs` | `meta.json` + five binaries → memory; the chunk guard |
| `PlyLoader.cs` | `.ply` → the same buffers, at load time |
| `SplatRenderer.cs` | GPU buffers, CommandBuffer construction, drawing, culling |
| `GpuSorting.cs` | 8-bit radix sort (near-unmodified upstream) |
| `ShaderBundle.cs` | fetches shaders from the AssetBundle |
| `SplatScene.cs` | one capture's lifetime, discovery and placement |
| `SplatBackdrop.cs` | the inward-facing black box |
| `TrackName.cs` | the loading track's name, through several fallbacks |
| `TrackBindings.cs` | reads and writes `bindings.json` |
| `TrackProbe.cs` | hunts strings inside the obfuscated game (F12) |
| `WebControl.cs` | HTTP server and the thread boundary |
| `WebUi.cs` | the browser UI (embedded HTML) |
| `Probe.cs` | runtime environment dump (F9) |
| `PerfLog.cs` | frame-time logging |
| `PostProcessFix.cs` | an attempt at the `-force-d3d12` side effect (**it does not work**; kept as a record) |

---

## If you extend it

- **Adding an API**: add a case to `WebControl.Handle()`, write the handler in `Plugin` and
  connect the delegate. Anything touching Unity must go through `QueueOnMain`
- **Adding UI**: append to `WebUi.Html`. **Dynamic values go in through `textContent`,
  always**
- **Reading game state**: change the needle and press F12. Do not trust constants from a
  decompile
- **Changing shaders**: rebake `unity/VDGSBundler` **on Windows**. A bundle under 1 MB
  means it failed
