# Adding a capture

*[日本語版](SCENES.ja.md)*

How to take a 3D Gaussian Splatting `.ply` and fly it in VelociDrone — including a
collision mesh so the drone stops at walls and floors.

The mod ships **no captures**. Bring a `.ply` you have the right to use. Academic datasets
and SuperSplat public scenes are other people's work; a missing licence is not permission
to redistribute. Flying a file you downloaded for yourself is a separate question from
publishing it.

Install and launch are in [USAGE.md](USAGE.md). This file is the capture pipeline.

---

## 1. Get a `.ply`

Shoot a place and reconstruct it (Postshot, Brush, the original 3DGS trainer, …), or
export from [SuperSplat](https://superspl.at/editor). The plugin reads **`.ply` only**,
not `.splat`.

**Shoot the floors.** A capture that only orbits an object reconstructs the ground as
gaussians stretched toward the camera. From above the floor looks filled; from a drone's
eye it dissolves. Walk the room. Point the lens at the floor.

Indoor rooms that were walked through fly well. Object-orbit captures (a plant on a table)
do not, and raising Y will not fix it.

---

## 2. Orient it

COLMAP-derived data comes in with an arbitrary orientation and scale, and **every capture
is mirrored in Unity** (right-handed Y-down vs left-handed Y-up). `PlyExporter` does not
fix this. The `.ply` has to arrive already standing up.

Do the aiming in SuperSplat's **orthographic** views (click the circle on the view cube):

1. floor level, ceiling a real height (about 2.4–2.7 m for a room)
2. export `.ply`
3. SuperSplat's export inverts Y. Check with `python3 tools/updir.py in.ply`, then:

```bash
python3 tools/align_ply.py in.ply out.ply --mirror y
```

`--rotate 180,0,0` cannot replace that reflection. Judge the result on text or something
left-right asymmetric, not on the subject. Full reasoning: [alignment.md](alignment.md).

Do not percentile-crop. The walls of a room shot from the inside *are* the outer shell.
Giant unconstrained gaussians (sky, anything past the camera path) are cut by **size**:

```bash
python3 tools/align_ply.py in.ply   # report only
python3 tools/align_ply.py in.ply out.ply --max-sigma 5
```

---

## 3. Drop it in and fly the picture

```
<VelociDrone>\app\vdgs\myscene.ply
```

Show it from `http://<host>:8777/` before you take off — the first load uploads tens of
megabytes and stutters. Bind it to a track when it looks right. Scale and height are on
the same page; rotation is not, that belongs in SuperSplat.

A `.ply` placed this way is Y-mirrored by the loader. If the capture is *already*
floor-down (you ran `--mirror y` and converted, or you corrected it by hand), convert it
instead of dropping the `.ply`, or it will stand on its ceiling. Details:
[ply-loading.md](ply-loading.md).

Conversion is optional. It makes the file smaller and the load faster. Use `High`:

```bash
Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
      -executeMethod PlyExporter.Run \
      -vdgsInput /abs/path/myscene.ply \
      -vdgsOutput /abs/path/build/splats/myscene \
      -vdgsQuality High -logFile -
```

Copy the output directory to `<game>\vdgs\myscene\`.

---

## 4. Bake a collision mesh

Without this the drone falls through the floor to VelociDrone's own ground. The picture is
gaussians; the collider is a triangle mesh built from their positions.

### What you need

`vdb_tool` from OpenVDB. **Linux or WSL.** Homebrew's `openvdb` formula does not build it.

```bash
# Ubuntu / WSL
sudo apt-get install -y python3-openvdb libopenvdb-tools python3-venv
python3 -m venv ~/vdgsvenv
~/vdgsvenv/bin/pip install numpy fast-simplification
```

macOS can do the steps around it (`align_ply.py`, `ply_points.py`, `glb_to_collision.py`).
The level-set bake and the first decimation have to run where `vdb_tool` is.

Also: `npx` (for `@playcanvas/splat-transform`) and the Python tools in `tools/`.

### Voxel size

One knob. Everything else in the pipeline follows it.

| | |
|---|---|
| Gap under the visual surface | ≈ **2 × voxel** |
| Wall thickness | ≈ **4 × voxel** |
| Physics | 400 Hz. At 150 km/h the drone moves 10 cm per step, so **walls thinner than 10 cm are tunneled** |

Finer = closer to the picture, thinner walls, more holes in sheets. Coarser = fatter
pillars, fewer holes. Start around **0.02–0.06 m** for an indoor room. An outdoor site
needs coarser. Then fly.

A factory floor at 0.06 had holes and was still the one that flew; 0.14 made the columns
too thick to accept. Drop tests are a diagnostic, not a pass/fail — the same mesh can fail
a scripted drop and hold a drone.

### Commands

Replace `myscene.ply` and `VOXEL`. Run the OpenVDB block on Linux/WSL so the 100–400 MB
intermediate mesh never has to cross the network.

```bash
# Clean-up (any machine with Python + npx)
python3 tools/align_ply.py myscene.ply clean0.ply --max-sigma 5
npx -y @playcanvas/splat-transform@3.3.0 -w clean0.ply \
    --filter-floaters --filter-cluster clean.ply
python3 tools/ply_points.py clean.ply points.ply

# Bake + decimate (Linux / WSL) — voxel is metres
VOXEL=0.04
vdb_tool -read points.ply \
  -points2ls voxel=$VOXEL radius=2.0 width=4 \
  -median iter=1 -open radius=1 \
  -ls2mesh adapt=0.9 -write fine.ply
python3 tools/decimate_mesh.py fine.ply reduced.ply 500000

# Islands off, glb, runtime blob (any machine)
python3 tools/clean_mesh.py reduced.ply mesh.ply \
    --voxel $VOXEL --min-voxels 100 --min-extent 0.25
python3 tools/mesh_to_glb.py mesh.ply collision.glb
python3 tools/glb_to_collision.py collision.glb myscene.collision.bin
```

`bash tools/preview.sh myscene 0.04` is the same pipeline plus a browser overlay. It
**fails if `vdb_tool` is missing** (and `VDGS_HOST` is unset so it cannot reach WSL). It
does not fall back to splat-transform's voxel mesher — that path was measured at about
8× the gap.

`--reverse` flips winding. Some rooms need it (otherwise you sit on the outside of the
shell); some break if you add it. Do not guess from signed volume. Enable **show solid**
in the Web UI: inside a correctly wound room the walls are visible, inside an inside-out
one they vanish. Then fly. Keep the flag that holds you up.

### Where the file goes

| Capture | Collision file |
|---|---|
| `<game>\vdgs\myscene.ply` | `<game>\vdgs\myscene.collision.bin` |
| `<game>\vdgs\myscene\` (converted) | `<game>\vdgs\myscene\collision.bin` |

Reload the capture after replacing the file. Toggling `solid` does not re-read it.

### In the Web UI

| | |
|---|---|
| `solid` | the mesh stops the drone. Off is fly-through. Mid-flight toggle is free after the first cook |
| `hide mesh` / `show solid` / `show wire` | draw the shell. Solid culls back faces (the winding test). Wire is the shape |
| scale / Y | live; written to `placement.json`. The collider is a child, so it follows |

A capture with no `collision.bin` has no checkbox — that means there is no mesh, not that
it is switched off.

Show the capture **before** you fly if `solid` is on. Cooking hundreds of thousands of
triangles shares the spawn stall with the splat upload.

---

## 5. Why a recipe, not a zip of scenes

The captures flown during development are not ours to ship. What we can ship is this
pipeline, plus `tools/make_test_ply.py` (a synthetic room for checking axes).

Numbers, failure modes and the discarded methods live in
[the collision design notes](superpowers/specs/2026-08-18-splat-collision-design.md).
This page is the part you run.
