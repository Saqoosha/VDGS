# Orienting a capture

*[日本語版](alignment.ja.md)*

**Data from COLMAP has an arbitrary orientation and an arbitrary scale.** `PlyExporter`
changes neither, so what needs fixing is the `.ply` going in, not the conversion. Do the
orienting in [superspl.at/editor](https://superspl.at/editor)'s orthographic views.

## 3DGS always comes out mirrored in Unity

**Every capture does.** It is not a property of any one file.

3DGS, being COLMAP-derived, is **right-handed and Y-down**; Unity is **left-handed and
Y-up**. Searching the whole of UnityGaussianSplatting turns up no axis conversion
anywhere — the .ply's coordinates are read as Unity coordinates directly, so the result is
always mirrored.

Watching the subject will not tell you. **Judge it on text, or on something
left-right asymmetric.**

The correct fix is a single reflection across Y:

```bash
python3 tools/align_ply.py in.ply out.ply --mirror y
```

Reflecting Y (determinant −1) **fixes the flip and the handedness at the same time**.
`--rotate 180,0,0` is a rotation (determinant +1), so it can correct which way is up while
leaving the mirror in place — a distinction that cost a long detour.

## Do not negate the quaternion's w when mirroring

Expressed on a quaternion, `R' = M R M` **negates the two components that are not the
mirrored axis, leaving w and the mirrored axis alone**:

```
mirror x -> (w,  x, -y, -z)
mirror y -> (w, -x,  y, -z)
mirror z -> (w, -x, -y,  z)
```

Negating w as well is tempting, since q and −q are the same rotation. **That identity only
holds when all four components flip.** Negating w on its own breaks in a specific way:

- **positions stay perfect**
- **every ellipsoid points somewhere else**

On screen the scene keeps its shape while scattering into needles, which makes it hard to
tell whether the fault is in position or orientation.

If you change the implementation, check it against the matrix — 200 random rotations
agreed with `M·R·M` exactly.

## SuperSplat exports with Y inverted

**A capture standing perfectly upright in the editor comes out upside down relative to
Unity.** Verified with a density histogram: the densest horizontal slice of a room is its
floor, and after a SuperSplat export that slice sits at the top.

`align_ply.py` checks for this and warns when the densest surface is in the upper 60% of
the range.

## Automatic floor detection does not work

Three RANSAC variants were tried and all three found a wall. Worse, they return
**plausible numbers** — a `tilt 1.4°` that turns out to be 1.4° against a wall, only
discovered after loading the scene into the game. Iterative inlier refinement made it
worse, not better: 11.9° → 23.7°, because each pass pulls harder toward whichever large
surface holds the most points.

**"The plane with the most points" and "the plane a human calls the floor" are different
things**, and the second is not determined by geometry alone. SuperSplat's orthographic
view is the right tool.

What `align_ply.py` is still for:

| Option | Use |
|---|---|
| `--mirror x\|y\|z` | The handedness fix above |
| `--rotate X,Y,Z` | Apply an angle read off a viewer. **Applied to the quaternions too** |
| `--max-sigma PCT` | Drop gaussians larger than PCT% of the scene. See below |
| `--sample N` | Thin the cloud for a preview that SuperSplat will open quickly |
| `--ceiling H` | Derive scale from a known ceiling height and drop the floor to y=0 |
| `--bounds` | State an explicit box, when debris needs excluding |
| ~~`--up`~~ | Automatic floor detection. **Does not work**; kept as a record |

A rotation has to be applied to **each gaussian's orientation quaternion**, not only to
positions. Rotating positions alone leaves a point cloud that looks right and every splat
tilted.

## Cutting the giants, by size and not by position

3DGS blows gaussians up wherever the training had nothing to constrain them — sky, and
anything past the end of the camera path. They come out enormous, half-transparent, and
sitting outside the capture. Measured on utlida-full (4,003,388 splats, extent 93.4):

```
sigma band       splats     share    median dist from centre   share of drawn area
0.0-0.2% of ext  3,757,600  93.86%          18% of extent             2.3%
0.2-1.0%           228,520   5.71%          33%                       6.3%
1.0-5.0%            15,702   0.39%          51%                      10.3%
5.0-20%              1,388   0.03%          88%                      20.6%
20-1000%               178   0.004%        148%                      60.5%
```

**178 gaussians — four thousandths of one percent — are 60% of everything drawn.** The
largest is 1.8 scene-extents wide. They are what "I can see very large splats" means, and
they are also why that capture dropped frames: pure overdraw.

```bash
python3 tools/align_ply.py in.ply out.ply --max-sigma 5
```

**Size is the right filter, and position is not.** A `--bounds` box would take the walls
of any room captured from the inside, and a connectivity filter cannot touch these at all:
a gaussian 1.8 extents wide overlaps everything, so it is connected to the main cluster by
construction. Only its size distinguishes it.

**Verify the threshold, do not pick one.** Rendered from fixed cameras and subtracted:

| | pixels lost | pixels gained | the lost pixels, in the original |
|---|---|---|---|
| `--max-sigma 5` | 0.00% / 12.57% | 0.000% | brightness 37, contrast 18 — against 62 and 26 for the kept ones |
| `--max-sigma 1` | 75.69% / 66.35% | 0.000% | brightness 88, contrast 22 — **the same as the kept ones** |

At 5 what disappears is dimmer and flatter than what stays: that is fog. At 1 what
disappears is as bright and as detailed as what stays: that is the scene. **Nothing was
gained at either setting**, so the filter introduces nothing.

**Only utlida needed it.** Across the other captures the same threshold removes 11-27% of
drawn area from splats that are all within 22% of extent — plausible background that
belongs in the picture. Scan before applying:

```
utlida-full    1,559 (0.039%)  83.7% of area   worst splat 179.8% of extent
utlida-lod1    1,054 (0.053%)  82.2%                        165.7%
calico-lod3      361 (0.015%)  27.3%                         19.4%
nelson-full      442 (0.005%)  15.3%                         21.5%
textilni-lod3     12 (0.0005%) 11.8%                         21.9%
nelson-lod2      131 (0.006%)  11.4%                         13.1%
```

Give no output path to get the report without writing a file.

## Cropping: don't

Percentile cropping trims the outer shell, and **a room photographed from the inside has
its walls there**, so it deletes the room rather than the debris. playroom lost 28% of its
splats — 540,000 — and visibly thinned.

`tools/crop_ply.py` and the cropped .ply files were deleted rather than kept: a tool whose
only effect is to degrade quality is a trap to leave lying around. If debris really is in
the way, name an explicit box with `align_ply.py --bounds`.
