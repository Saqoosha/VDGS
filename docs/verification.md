# Verifying what gets rendered

*[日本語版](verification.ja.md)*

**Looking at the picture has never once caught a defect in this project.** A mirrored
scene, a leftover chunk buffer and an orthographic camera each produced something that
looked like a plausible capture. So measure instead.

## Orientation: `tools/verify_orientation.py`, not your eyes

An orientation error **does not show**. It reads as a slightly hazier version of the same
scene, and by the time the symptom appears — needle-shaped highlights — the conversion and
the deploy are already done. Check every gaussian numerically:

```bash
python3 tools/verify_orientation.py build/testdata/bonsai2-aligned.ply \
        build/splats/bonsai --mirror y --sample 40000 --cell 0.02
```

It reconstructs each gaussian's ellipsoid frame from both the source .ply and `other.bin`
and measures the angle between corresponding axes. Measured:

| Scene | mean | p99 | max |
|---|---|---|---|
| bonsai 1,157,141 | 0.102° | 0.200° | 0.311° |
| drjohnson 3,177,554 | 0.103° | 0.220° | 0.368° |
| luigi 14,526 | 0.099° | 0.182° | 0.285° |

**Zero is not the expected answer.** Rotation is always packed to 10-bit smallest-three
regardless of quality level, which puts a floor near 0.18°. The measurements sit on it.
Anything past 1° is a real defect.

`--mirror y` applies the pipeline's own transform to the *source*, so pass it only when
the .ply has not been mirrored yet. For an already-mirrored file, leave it off. The floor
translation `align_ply.py` adds is compensated automatically.

Three traps, each of which returns a confident wrong answer:

- **The converter reorders splats spatially.** Comparing index to index is meaningless;
  match by position.
- **The decoded float4 is `(x,y,z,w)`**, not the .ply's `(w,x,y,z)`. Reading it the other
  way gives about 38° of average error, which looks like a real bug.
- **Real 3DGS .ply files store unnormalised quaternions** — bonsai's sit around 0.98. The
  rotation-matrix formula assumes unit length, so feeding them raw produces 22° of phantom
  error. Synthetic test data has unit quaternions and sails straight past this.

Synthetic data with known orientations comes from `tools/make_orient_ply.py`: red along
+X, green +Y, blue +Z, yellow along the diagonal, and a fan in the XY plane whose sweep
direction reverses under a mirror.

## Pixels: subtract an independent renderer

```bash
bash tools/compare_with_webref.sh bonsai build/testdata/bonsai2-aligned.ply 1.035,-1.145,-54.8
```

The reference is [antimatter15/splat](https://github.com/antimatter15/splat) — MIT, a
single-file WebGL viewer. It is fetched into `build/webref/` rather than vendored.

Measured at 1024×1024, focal 1920:

| Scene | splats | matching orientation | coverage IoU | mean delta /255 |
|---|---:|---|---:|---:|
| luigi | 14,526 | flip-y | 0.9376 | 8.16 |
| bonsai | 1,157,141 | flip-y | 0.9389 | 9.79 |

**A correct pair shows a black interior in the difference image with only the silhouette
edges lit.** If the interiors light up, something systematic is wrong.

Three things to know:

- **The reference uses the original 3DGS convention (right-handed, Y-down)**, so its image
  always matches after a vertical flip. `compare_renders.py` searches all eight
  orientations and reports the winner rather than assuming one.
- **A left-right symmetric subject cannot be resolved by coverage alone** — Luigi scores
  IoU 0.92 even at rot180. Colour breaks the tie, and that is implemented.
- **The viewer's bundled camera has `fx=1159.59, fy=1164.66`.** That 0.4% difference is
  four pixels at 1024 wide and swamps the comparison; a local patch forces both to one
  value via `?f=<focal>`.

## Never render 3DGS with an orthographic camera

Building comparison views to match SuperSplat's view cube, an orthographic camera seemed
obvious. **It was manufacturing its own bug.** The shader builds a perspective Jacobian:

```hlsl
float focal = screenParams.x * matrixP._m00 / 2;
J = { focal/z, 0, -focal*x/z^2, ... };
```

Under an orthographic projection `_m00` is not a tangent-of-fov and the `1/z` terms mean
nothing. Every splat gets the wrong size and a spurious shear. **No error is raised — it
just goes soft**, and the softness gets blamed on the data.

`RenderViews` and `RenderCompare` use a very narrow perspective (4°) from far away
instead: orthographic in all but name, with the shader's arithmetic intact.

Other practical notes:

- **Call `Camera.Render()` twice.** The sort is queued into the same command buffer as the
  draw, but nothing guarantees the first frame is settled. Grainy noise is the symptom.
- **1024 aliases on real captures.** Fine for comparison, but use `-vdgsSize 2048` for
  anything you intend to look at.
- **Chrome's `--screenshot` cannot capture the reference viewer.** It renders in a
  `requestAnimationFrame` loop that never goes idle, so `--virtual-time-budget` hangs
  instead of finishing. `webref_shot.mjs` drives CDP directly and waits for the viewer to
  hide its spinner (Node 24's native WebSocket, no dependencies).

## Anisotropy alone cannot tell a plate from a needle

`max/min` gives 100 for both `(1, 1, 0.01)` — a wall or floor, which is normal — and
`(1, 0.01, 0.01)`, which is not. **The middle axis separates them:**

```
t = (log(mid) - log(min)) / (log(max) - log(min))     t≈0 is a needle, t≈1 is a plate
```

Measured across four outdoor captures, 81–86% of the highly anisotropic splats are plates
and essentially none are needles. **Reporting "a third of this scene is needles" from the
max/min ratio was wrong.**

Seen from directly above, a vertical plate is edge-on and reads as a bright line. That
looks like corruption and is not — the same rendering from a drone's eye view shows walls.
