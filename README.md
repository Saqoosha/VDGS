# VDGS

*[日本語版](README.ja.md)*

A mod that renders 3D Gaussian Splatting captures inside VelociDrone — so you can fly a
real, scanned place in an FPV drone simulator.

[![flying a scanned place inside VelociDrone](docs/vdgs.jpg)](https://www.youtube.com/watch?v=MuDq_7X-4Mo)

[Watch the flight](https://www.youtube.com/watch?v=MuDq_7X-4Mo)

## Getting it

Download **vdgs-companion** from the
[latest release](https://github.com/Saqoosha/VDGS/releases/latest) and run it. It finds
VelociDrone, installs the mod, fetches a capture and its course, and launches the game
with the flag the shaders need — four clicks, no checkout of this repository.

You need VelociDrone 1.16.0 on Windows and a GPU that can do D3D12; the mod always
launches with `-force-d3d12`, because the sort compute uses SM6 wave intrinsics and the
shaders bake as unsupported without it. Captures are fetched from inside the app, not from
here — they are hundreds of megabytes each. Step-by-step: [docs/USAGE.md](docs/USAGE.md).

## What it does

| | |
|---|---|
| Largest capture flown | **4,508,391 splats** (FDF-2026-08-22). Three scenes at once, 1.17M in total, is also fine |
| Frame time | **9.0 ms** at 3.18M splats (drjohnson) — RTX 3060, drone's eye view, 120° field of view |
| Collision | You can hit the scene — 2,197,134 triangles baked from FDF-2026-08-22. Physics runs at 400 Hz, so a wall thinner than 10 cm is passed through at 150 km/h |
| Depth | Correct against gates and the aircraft; alpha blending holds up |

Palette-free SH packing and frustum culling took that from 13.3 ms to 9.0 ms, **neither of
them costing image quality**. The breakdown is in [docs/performance.md](docs/performance.md).

## How it works

```
drop <game>/vdgs/foo.ply  →  the plugin reads it at load time and renders it
                             ↑ drive it from a browser at http://<host>:8777/
```

No external tools. A 2.17M-splat capture parses in under a second and is on screen in
about three; a four-million-splat capture with spherical harmonics takes thirteen, so show
a scene before you fly rather than during. Pre-converted scenes (five binaries plus
`meta.json`) still work if you have them.

The plugin polls the loaded track name once a second and shows or hides captures according
to `bindings.json`, so a scene appears on the tracks you bound it to and nowhere else.

Rendering is a port of [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
(MIT), stripped of editing, the URP/HDRP paths and the Burst dependency so it runs inside
an injected plugin.

## Documentation

| | |
|---|---|
| [docs/USAGE.md](docs/USAGE.md) | Install, launch, control it |
| [docs/SCENES.md](docs/SCENES.md) | Bring a `.ply`, orient it, bake a collision mesh |
| [docs/TRACKS.md](docs/TRACKS.md) | Lay a course over a capture and get it shippable |
| [docs/distribution.md](docs/distribution.md) | The companion app, the release run, the hosting |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Internals, and why the design is what it is |
| [docs/ply-loading.md](docs/ply-loading.md) | Reading .ply at load time, and its traps |
| [docs/performance.md](docs/performance.md) | Where the frame time goes, and what moves it |
| [docs/verification.md](docs/verification.md) | Checking the render with numbers, not eyes |
| [docs/alignment.md](docs/alignment.md) | Orienting a capture, and the mirror everyone hits |
| [AGENTS.md](AGENTS.md) | Measured traps (Japanese). No machine-specific paths |

## Requirements

- VelociDrone, built on Unity 2021.3.45f2
- **A D3D12-capable GPU.** Sorting splats needs Shader Model 6 wave intrinsics, so DX11
  will not do. Launch the game with `-force-d3d12`
- BepInEx 5.4.23.5 (win_x64)

Building the mod additionally needs Unity 2021.3.45f2 **on Windows** for the shader bundle —
macOS cannot compile D3D shaders. Adding a capture needs nothing: drop the `.ply` in.

## Please do not

**Use this on leaderboards or in multiplayer.** VelociDrone ships
`ACTk.Runtime.dll` (Anti-Cheat Toolkit), and submitting times from a modified client is
against its terms. Local flying only.

## Captures

**Two are published, and the companion installs them.** Both were scanned for this project
and released under CC0, and both come with a collision mesh.

| | splats | where |
|---|---|---|
| FDF-2026-08-22 | 4,508,391 | Funabashi Drone Field, an FPV practice field in Funabashi, Chiba |
| JDL-2026-R5 | 2,521,003 | the Japan Drone League 2026 Round 5 race site, Okayama |

**Nothing else flown during development can be redistributed, and none of it is here.**
The academic datasets state no licence at all, which means all rights reserved rather than
"free"; the INRIA 3DGS licence is research-only and carries that restriction into
derivative works; the rest are other people's captures published on SuperSplat under
whatever their author chose. The only splat data in this repository is what
`tools/make_test_ply.py` generates — a synthetic scene for checking axes, colour and scale.

Bring your own `.ply` and drop it in `<game>/vdgs/`. Collision meshes are baked locally —
[docs/SCENES.md](docs/SCENES.md).

## Licence

The project is [MIT](LICENSE). `src/VDGS/GpuSorting.cs` and
`unity/VDGSBundler/Assets/VDGS/Shaders/` additionally derive from
[aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) (MIT).
The GPU sort in turn derives from [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting)
(MIT, Thomas Smith).
