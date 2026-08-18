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
| `--sample N` | Thin the cloud for a preview that SuperSplat will open quickly |
| `--ceiling H` | Derive scale from a known ceiling height and drop the floor to y=0 |
| `--bounds` | State an explicit box, when debris needs excluding |
| ~~`--up`~~ | Automatic floor detection. **Does not work**; kept as a record |

A rotation has to be applied to **each gaussian's orientation quaternion**, not only to
positions. Rotating positions alone leaves a point cloud that looks right and every splat
tilted.

## Cropping: don't

Percentile cropping trims the outer shell, and **a room photographed from the inside has
its walls there**, so it deletes the room rather than the debris. playroom lost 28% of its
splats — 540,000 — and visibly thinned.

`tools/crop_ply.py` and the cropped .ply files were deleted rather than kept: a tool whose
only effect is to degrade quality is a trap to leave lying around. If debris really is in
the way, name an explicit box with `align_ply.py --bounds`.
