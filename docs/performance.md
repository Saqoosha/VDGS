# Where the frame time goes

*[日本語版](performance.ja.md)*

The problem: drjohnson (3.18M splats) made the RTX 3060's fan audible. This is what was
measured and what moved it, **without giving up image quality**.

**Two things worked: choosing the right format tier, and frustum culling.** Together they
took drjohnson from 14.4 ms to 9.0 ms in the benchmark, and 26.8 ms to 17.3 ms in the game.

**And the biggest lesson: a conclusion measured on the wrong machine did not transfer.**
The same comparison gave 6.5% on an M1 Max and 48% on the RTX 3060, because unified memory
hides bandwidth a discrete card has to pay for.

---

## 1. Locating the cost

The instrument is `RenderBench` in `unity/VDGSBundler`. It reads one pixel back after
`Camera.Render()` to force a GPU sync, so each iteration is a real frame.

### RTX 3060 / D3D12 — the machine that matters (`bash tools/bench-win.sh`)

```
                splats   B/splat    total     splat cost above the 4.16 ms floor
empty                0        -    4.16 ms          -
bonsai           1.16M      236    6.66 ms      2.50 ms   (2.16 ms/M)
playroom         1.92M       84    8.24 ms      4.08 ms   (2.13 ms/M)
drjohnson        3.18M      236   13.92 ms      9.76 ms   (3.07 ms/M)
drjohnson-shc    3.18M       46    9.20 ms      5.04 ms   (1.59 ms/M)
```

### The harness had the same bug as the conclusion

It originally read the whole 1024×1024 target back every frame to force the sync. That is
free on unified memory and **dominant across PCIe**: an empty frame measured 17.55 ms on
the RTX 3060 — slower than drawing two million splats. **Reading a single pixel syncs just
as hard** and transfers four bytes.

### Breakdown

| Component | How it was isolated | Measured | Share |
|---|---|---:|---:|
| SH bandwidth | same geometry, 5.1× less data | 2.1 ms | 7% |
| Sorting | `m_SortNthFrame` 1 vs effectively off | 2.0 ms | 6% |
| Pixel work | 1024 → 2048, four times the pixels | ~2 ms | 6% |
| **Per-splat fixed cost** | the remainder | **~28 ms** | **87%** |

```
sortNth=1  32.19 ms      512px  50.52 ms  ← outlier; the GPU never clocks up
sortNth=2  31.08 ms     1024px  32.19 ms
sortNth=4  30.38 ms     2048px  34.19 ms  ← four times the pixels for +6%
sortNth=∞  30.19 ms
```

**"Bandwidth dominates" was concluded once and was wrong.** It fitted three in-game points
from different scenes, framings and overdraw. A controlled comparison — same geometry,
same camera, 5.1× less data — moved the frame by 6.5%, and that refuted it. Vary one
thing at a time.

### What the 87% is

The code says it plainly:

- `SplatRenderer` drew **every splat unconditionally** as an instanced quad, with no
  compaction to the visible ones
- `CSCalcViewData` spawns **a thread per splat**, projecting, building the 2D covariance
  and evaluating SH
- there was no frustum culling at all, only a behind-camera test

**All 3.18M paid every frame, visible or not.**

---

## 2. What worked, at no cost to quality

### 2.1 Frustum culling with an indirect draw

**Measured: 10.49 → 9.37 ms on drjohnson-shc from inside the scene at 120°, three runs
each. Every pixel identical.**

`m_FrustumCulling`, on by default. How it works:

1. `CSCalcDistances` tests each splat against the clip-space frustum. **This is where it
   belongs, because this is where draw order is decided**
2. A culled splat gets **the maximum sort key**. The sort is ascending, so it parks past
   everything visible
3. `DrawProceduralIndirect` draws only the visible count, accumulated **one atomic per
   wave** — three million contended increments would cost more than the cull saves

That removes it from the rasteriser and from `CSCalcViewData`'s useful work without a
separate compaction pass.

#### The margin comes from each splat's own size

The test is on splat **centres**, so a splat whose centre has left the view but whose
skirt has not must still be kept. How much margin was measured, never chosen: raise it
until the culled image matches the unculled one exactly.

A single global margin has to cover the largest gaussian anywhere in the capture, and
captures hold a few enormous diffuse ones, so it needed to be **8 screen half-widths**:

```
margin 0.5   mean difference 11.48/255   0.2% of pixels identical
margin 2      1.78                       6.6%
margin 8      0.00                     100%
```

Deriving it per splat brings it down to **sigma 4** — two drawn quads' worth of slack,
since the vertex shader emits ±2σ:

```
sigma 1   2.78068/255     5.7% identical
sigma 2   1.06876/255    34.8%
sigma 3   0.00072/255    99.8%
sigma 4   0.00000/255   100%
```

##### Do not read the radius per splat; it scatters

Calling `LoadSplatScale(origIdx)` in the distance pass is correct **and slower than the
cull it buys**: the index comes from the sorted key buffer, so the reads scatter across
all 57 MB of drjohnson's `other.bin` every frame.

A table of the largest radius per 256 splats, built once in index order where reads are
sequential, is **50 KB** for three million splats and stays in cache.

##### Two errors in the derivation

**Clip-space frustum planes are not unit length.** For the side planes the gradient with
respect to view position is `(P00, 0, -1)`, so a sphere of radius r crosses at
`r·√(P00²+1)`, not `r·P00`. At 120° that is a factor of two.

And the distance pass set `_SplatFormat` to **the position format alone**. `LoadSplatPos`
only reads the low byte, so it never mattered — until `LoadSplatScale`, which derives the
`other.bin` stride from the upper bytes. A 16-byte stride against 18-byte data reads a
different splat's scale every time. **The symptom was a cull that ignored its own margin:
raising the multiplier twentyfold changed nothing.** When a parameter sweep produces no
response, suspect the parameter is not reaching the computation.

#### What it is worth

**10.7%, pixel-identical.** The predicted 22% did not materialise. All three lossless
variants land between 9.0 and 9.4 ms against a ~10.5 ms baseline and the differences sit
inside the noise; the chunk-radius version ships because its margin is tightest and its
timings are the most repeatable (0.7% spread against 9%).

Its effectiveness depends entirely on where the camera looks. On drjohnson at 120°,
between 41% and 97% of the capture falls inside the frustum.

### 2.2 Choosing the format tier — measure, do not count bytes

Every tier on drjohnson. RTX 3060, inside at 120°, culling on, reference is Float32:

| Tier | B/splat | frame | mean delta vs Float32 | k-means |
|---|---:|---:|---:|---|
| VeryHigh (Float32) | 236 | 14.01 ms | — | no |
| VeryHigh + Cluster16k SH | 47 | 9.38 ms | 1.44/255 | **10 minutes** |
| **High (Norm16 / Float16x4 / Norm11)** | **84** | **8.80 ms** | **0.09/255** | **no** |
| Medium (Norm11 / Norm8x4 / Norm6) | 48 | — | **58.83/255** | no |

**High is the most faithful and needs no k-means.** It carries 1.8× the bytes of clustered
SH and still benches faster — but that last part does not survive contact with the game.

### Between High and Cluster16k, pick on size — nothing else separates them

**Speed: unmeasurable in flight.** Both tiers flown back to back, one session, one build,
labelled in the log:

```
drjohnson-high   n=9   median 13.99 ms   mean 12.74   range 8.52-14.91
drjohnson-shc    n=8   median 13.62 ms   mean 13.37   range 11.78-14.86
```

**The median and the mean disagree on which is faster** (t = -0.75). The spread *within*
one scene is 6.4 ms, driven by where the camera points; the tier gap the bench reports is
0.20 ms. Detecting a 0.2 ms effect against a 2 ms standard deviation needs roughly 1600
samples a side — **2.2 hours of flying per tier**. This will not be settled by flying, and
a doc that implies otherwise costs somebody an evening.

**Appearance: also indistinguishable.** Same camera, three views, subtracted:

```
view       mean |delta|   p99   max   pixels >8/255
fwdZ           1.333     2.33     5       0.0%
fwdX           1.034     2.00     3       0.0%
fwdNegZ        0.786     2.33     4       0.0%
```

**The single worst pixel in a 1024×1024 frame differs by 5 levels out of 255**, and no
pixel anywhere exceeds 8. The fidelity figures against Float32 — High 0.09/255, Cluster16k
1.44/255 — are both below what an eye resolves; 0.09 only means something when Float32 is
the thing on the other side of the comparison.

So the decision is about size, and one workflow constraint:

| | High | Cluster16k |
|---|---|---|
| formats | Norm16 / Norm16 / Float16x4 / Norm11 | **Float32 / Float32 / Float32x4** / Cluster16k |
| bytes/splat | 84 | 47 |
| drjohnson on disk | 260 MB | 146 MB |
| VRAM at 3.18M | 267 MB | 149 MB |
| conversion | one pass | **~10 min of k-means** |
| runtime .ply can emit it | yes | **no** |

**Cluster16k does not compress geometry at all** — positions, scales and colour stay
Float32 and only the harmonics are palettised, so its whole 1.44/255 is SH error.

`reprocess.sh` defaults to High because it is the low-friction path. **Reach for
`-vdgsShFormat Cluster16k` deliberately when disk or VRAM is the constraint** — 44% less
of both, for a difference nobody can see and nobody can measure in flight.

The bench's ordering has a plausible mechanism — clustered SH reads a two-byte index per
splat and scatters into a 3.1 MB palette, which costs more than reading Norm11 SH in
sequence, and playroom-shc benched slower than playroom (8.64 vs 8.01 ms) the same way.
**Take that as an explanation of a bench result, not of anything you will feel.** The
effect it explains is 0.20 ms, and the game cannot resolve it.

What does survive is the negative: **"fewer bytes is faster" does not hold.** Cluster16k
is 44% smaller and is not faster. Frame time is dominated by per-splat work, not by how
much each splat weighs — which is the same lesson as §1, arrived at from the other side.

#### Do not use Medium or below

`Norm11 / Norm8x4 / Norm6` renders drjohnson **2.6× too dark** (brightness 36.3 against
95.1, mean difference 58.83/255). The geometry is right (IoU 0.9958), so the fault is in
colour or opacity. Whether it is upstream's tier or this port is **unresolved** — testcube
uses the same formats and looks correct, so it may be data-dependent.

#### Trap: `posFormat` says nothing about coordinate space

Baking with clustered SH emits `chunk.bin` and makes positions **chunk-relative 0..1
weights while `posFormat` still reads `Float32`**:

```
drjohnson       pos range  -23.186 .. 15.099   ← absolute
drjohnson-shc   pos range    0.000 ..  1.000   ← chunk-relative
```

`Float32` is a **storage width**, not a claim about the coordinate space. A guard that
inferred "Float32 means absolute" and discarded the chunk data collapsed the whole capture
into a blob at the origin — as silent as the scattered debris it was written to prevent.
Only the conversion knows, so `PlyExporter` writes `chunkCount` into `meta.json` and the
loader checks against it.

### 2.3 Reading .ply at load time

Not a rendering optimisation, but it changes the economics: the runtime loader produces
132 B/splat and lands **7% behind the best baked format**, with no offline step at all.
See [ply-loading.md](ply-loading.md).

---

## 3. What is capped by measurement

### Exact tile intersection (rasteriser side, lossless)

The reference CUDA implementation rasterises **per tile**, estimating which tiles a splat
touches with a conservative rectangle. Tightening that costs nothing in image quality:

- [StopThePop](https://r4dl.github.io/StopThePop/) (SIGGRAPH 2024) — hierarchical
  rasteriser with tile-based culling that drops non-contributing gaussians before blending,
  and fixes popping at the same time
- [FlashGS](https://www.researchgate.net/publication/394512353_FlashGS_Efficient_3D_Gaussian_Splatting_for_Large-scale_and_High-resolution_Rendering)
  — exact ellipse/rectangle intersection, redundancy elimination, adaptive scheduling;
  claims an order of magnitude
- [Speedy-Splat](https://speedysplat.github.io/) (CVPR 2025) — precise tile allocation,
  **2× on rasterisation**

**The shared key is opacity-aware bounds** — cut the ellipse where α falls below a
threshold rather than at a fixed σ.

**Our shader emits a fixed-size quad (`quadPos *= 2`) regardless of opacity**, so the room
is there. But pixel work measures 6% of the frame: unlike the CUDA reference, **our
bottleneck is not rasterisation**, so this family is capped low.

### Dropping the global sort

The reference keeps a sorted list per tile; we run **one global sort over every splat**.
Sorting measures 6%, so fixing it cannot return much.

- [Hybrid Transparency](https://arxiv.org/pdf/2410.08129) — perspective-correct blending
  without a full sort

---

## 4. LOD — needs retraining

Reduces distant splats. **Near-field quality is preserved**, so it is close to lossless in
the sense that matters, but **none of it can be applied to an existing .ply** — they all
change the structure of training itself.

- [Hierarchical 3D Gaussians](https://repo-sam.inria.fr/fungraph/hierarchical-3d-gaussians/)
  (INRIA, SIGGRAPH 2024) — trains chunks independently and consolidates into a hierarchy,
  optimising the gaussians merged into interior nodes; includes level selection and smooth
  transitions
- [Octree-GS](https://city-super.github.io/octree-gs/) (TPAMI 2025) — anchor gaussians per
  octree level, accumulated from coarse to fine per view, arranged by a grow-and-prune and
  progressive training scheme
- [LODGE](https://arxiv.org/abs/2505.23158) (NeurIPS 2025) — depth-aware smoothing,
  importance pruning and fine-tuning per level, plus dynamic loading of spatial chunks to
  cut GPU memory; opacity blending hides the chunk seams
- [HiGS](https://arxiv.org/html/2606.00352v1) — a hierarchical rendering architecture

### Inside one room

**Probably limited, unverified.** LOD pays where the distance range is large — metres to
kilometres in a city. drjohnson spans at most about 40 units and a tinywhoop flies a
fraction of that; the far wall still covers a fair number of pixels.

That said, Octree-GS's premise — that too many primitives inside the frustum is the
bottleneck — matches what was measured here. **The prerequisite it rests on, drawing only
what is inside the frustum, came first** (see 2.1).

---

## 5. Reducing splat count (costs quality; out of scope)

Recorded only, since the instruction was not to trade quality. The literature reports 90%+
pruning with little visible change:

- [REFINE](https://arxiv.org/html/2606.09074) — rendering-free importance estimation,
  **applicable to an already-trained model**
- [3DGS.zip survey](https://onlinelibrary.wiley.com/doi/10.1111/cgf.70078?af=R) (CGF 2025)
  — the landscape of compression and pruning, with comparisons

"Little visible change" is a judgement call, so **if you try it, quantify it with
`compare_renders.py`** — IoU and mean pixel difference. Do not decide by eye.

---

## 6. Practical conclusion

The reliable lever now is **keeping a capture under about two million splats**. playroom
is 1.92M at 8.24 ms; drjohnson is 3.18M at 8.80 ms on High; in-game, 3M is roughly where
60 fps sits. Everything else is capped by the measurements above.

| # | Lever | Quality | Worth | State |
|---|---|---|---|---|
| 1 | frustum culling + indirect draw | lossless | 10.7%, view-dependent | **shipped** |
| 2 | the High format tier | most faithful | 37% against Float32 | **default** |
| 3 | opacity-aware quad shrinking | lossless | small; pixel work is 6% | no |
| 4 | sorting less often | more popping | 6% ceiling | no |
| 5 | LOD | far field only | probably small in one room | needs retraining |
| 6 | pruning | lossy | linear in what is cut | out of scope |

## How to measure

```bash
UNITY=/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity
"$UNITY" -batchmode -quit -projectPath unity/VDGSBundler -executeMethod RenderBench.Run \
  -vdgsScene "$PWD/build/splats/<name>" -vdgsSize 1024 -vdgsFrames 120 -logFile - | grep BENCH
```

`-vdgsScene none` measures the floor. `-vdgsSortNth N`, `-vdgsSize`, `-vdgsCull`,
`-vdgsCullMargin` and `-vdgsInside` vary one thing at a time. **Framing the whole capture
culls nothing, so `-vdgsInside 1` is the only way to measure culling.**

On Windows, `bash tools/bench-win.sh [scenes]` does the same against the RTX 3060 and also
accepts a bare `.ply`, reporting its load timings.

In-game numbers land in `<game>/vdgs-perf.log` every five seconds:
`time / fps / avg_ms / worst_ms / splats / scenes`. **That is the number decisions rest
on**; the benchmark exists to isolate variables.

The log **survives a relaunch** — each run appends under a `=== session <date>` banner.
It used to truncate on startup, which destroyed the only thing it is for: comparing a
change against the run before it. Any A/B that needs quitting the game between halves —
which is most of them — was silently losing its baseline.

Two traps when reading it:

- **The splat count does not identify the scene.** A `--- shown: <name>` line marks each
  change of displayed scene; use that. `drjohnson-high` and `drjohnson-shc` are **both
  3,177,554**, and the run comparing exactly those two turned out to be unreadable
  afterwards.
- **A number pinned to a ceiling means "not measured", not "fast".** "Exactly 16.67 ms,
  exactly 60.0 fps" was recorded as a VSync ceiling for a long time.
  **It was Parsec in the way.**
  Measured locally it is **119 fps / 8.40 ms** on a 120 Hz display.
  The first data not hidden under a ceiling:

```
utlida-full-s5   4,001,829 splats   12.04 ms   p90 13.99
utlida-lod1-s5   2,000,640 splats    9.13 ms   p90 13.39
```

**Twice the splats for 2.91 ms.** While both were pinned, that difference was invisible.
