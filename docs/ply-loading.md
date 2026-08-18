# Reading .ply at load time

*[日本語版](ply-loading.ja.md)*

Drop `<game>/vdgs/foo.ply` in and fly it. No Python, no Unity.

Pre-converted directories (`<name>/meta.json` plus five binaries) still work. **If both
exist under the same name, the directory wins** — it is already packed, so re-reading the
.ply would only be slower.

Placement is saved beside the .ply as `<name>.placement.json`.

## Why this can happen at load time

The ten minutes a conversion takes are **entirely k-means, clustering spherical
harmonics**. A .ply body is a packed array of fixed-size rows, so reading it is close to a
memcpy.

Measured on the RTX 3060 host — `bash tools/bench-win.sh <name>` reports load timings:

```
nelson       2,171,895 splats  121.6 MB   header 4 ms  read  99 ms  decode  870 ms   0.97 s
nelson-full  8,759,558 splats  490.5 MB   header 2 ms  read 265 ms  decode 3078 ms   3.34 s   (M1 Max)
```

**Reading the file is 100–265 ms of that. The rest is decode**, which is per-splat and
independent, so it runs across cores (`Parallel.For`).

### That is the parse. The stall you feel is about three times bigger

`PlyBench` times header, read, decode and sort. **It stops before
`GraphicsBuffer.SetData`**, and the upload is not free — so quoting 0.97 s as "the load
time" understates what happens on screen. Measured in-game from `vdgs-perf.log`, where the
spawn frame shows up as `worst_ms`:

```
                splats     SH   file      in-game stall
nelson-lod2  2,171,895   none   121.6 MB      2.95 s
calico-lod3  2,401,279   none   134.5 MB      3.10 s   (twice, identical)
nelson-full  8,759,558   none   490.5 MB     11.97 s
utlida-lod1  2,001,694   deg 3  472.4 MB      6.77 s
textilni     2,320,155   deg 3  547.6 MB      8.00 s
utlida-full  4,003,388   deg 3  944.8 MB     13.0 / 13.2 / 14.0 s
```

Two things fall straight out:

- **The rate is per splat, not per byte, and it is remarkably stable**: 1.34 µs/splat
  without spherical harmonics, 3.39 µs/splat with degree 3. Every scene above lands within
  4% of its group.
- **Spherical harmonics cost 2.5× the load time.** Per byte, an SH capture actually loads
  *faster* (~70 MB/s against ~42), because the per-splat work is amortised over more data.

So a four-million-splat capture with harmonics takes **thirteen seconds** to appear. The
advice to show a scene before flying is not a nicety — at that size it is the difference
between a stutter and a stopped game.

### Parallelising made it slower until the allocations were gone

`Put` originally went through `BitConverter.GetBytes`, which returns a fresh `byte[4]`.
The loop writes about ten floats per splat — **over twenty million allocations** for a
2.17M capture.

Single-threaded that is merely slow. **Across cores it was twice as slow** (3154 → 6334 ms),
because the threads contend on Mono's allocator instead of decoding. Writing the bytes
through an explicit-layout union brought it to **870 ms**.

```
allocating, single-threaded   3154 ms
allocating, parallel          6334 ms   ← parallelism backfired
allocation-free, parallel      870 ms
```

**Do not read "it got slower when I parallelised it" as the limit of parallelism.** The
allocator was the bottleneck.

## Output format and speed

Positions and scales stay Float32; colour and spherical harmonics are half precision.
**No chunks** — every format below Float16 stores 0..1 weights that only mean something
through a chunk's min/max, which would drag in a Morton sort and per-chunk bounds.

RTX 3060, drjohnson 3.18M splats, camera inside the scene at 120°, culling on:

```
Float32 everything       236 B/splat   14.42 ms
this loader              132 B/splat    9.98 ms
High, baked offline       84 B/splat    9.34 ms
```

**0.64 ms (7%) behind the best baked format.** Full High packing (Norm16 + chunks +
Morton) would close it at the cost of five more encodings; that trade looks poor.

Image quality against the Float32 converter on luigi: **0.0162/255**. Half precision costs
nothing measurable.

## Three traps, each silent when wrong

### Resolve properties by name. Fixed offsets die instantly

**A .ply's attribute list is not fixed.** Real files on hand:

| Source | Layout |
|---|---|
| INRIA standard | `x,y,z, nx,ny,nz, f_dc_0..2, f_rest_0..44, opacity, scale_0..2, rot_0..3` (62 floats) |
| `drjohnson-aligned.ply` | no normals (59 floats) |
| `splat-transform` output | `x,y,z, rot_0..3, scale_0..2, opacity, f_dc_0..2 [, f_rest_0..44]` (14 or 59 floats) |

**`splat-transform` puts rot before scale and `f_dc` last**, nothing like the standard
order. `PlyLoader` reads the header's property list and looks fields up by name.

### The colour texture is Morton-swizzled inside 16×16 tiles

It has to match `SplatIndexToPixelIndex` in the HLSL. Writing it linearly **scrambles
colour and alpha in 16×16 blocks** while the geometry stays perfect — so it does not look
like a geometry bug, and it is easy to blame on something else.

### .ply groups f_rest by channel; the shader reads it by coefficient

The file stores 15 reds, then 15 greens, then 15 blues. The shader reads `sh1.rgb,
sh2.rgb, …`. **Transpose it, or every band comes out in the wrong colour.**

## Captures with no spherical harmonics

Handheld LiDAR scanners such as XGRIDS PortalCam emit **degree 0** — the .ply has no
`f_rest_*` at all (`luigi.ply` is the same).

When `_SplatSHOrder` is 0 the shader skips the SH read entirely and the loader allocates
16 bytes instead of 192 per splat. **The effect is not a rounding error:**

```
nelson-lod2 (2.17M splats)   sh.bin  417 MB → 16 bytes; whole scene 92 MB against a 116 MB .ply
nelson-full (8.76M splats)   avoids a single 1.68 GB array
```

1.68 GB is 78% of `int.MaxValue`. **That is what makes 8.76M splats load at all** —
measured at 1.29 GB peak RSS and 316 MB of buffers.

## How to verify it

Compare against the converter **under the same transform**. `PlyLoader` mirrors Y by
default, so turn that off when comparing with the converter's raw output:

```bash
# bake the raw .ply with no mirror and no floor grounding
Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
  -executeMethod PlyExporter.Run -vdgsInput <raw>.ply -vdgsOutput build/splats/<x> -vdgsQuality VeryHigh

# render both from one camera and subtract; -vdgsPlyNoMirror matches the transforms
Unity ... -executeMethod RenderCompare.Run -vdgsScene <raw>.ply -vdgsPlyNoMirror 1 ...
```

**Getting that wrong once produced a 27.9/255 mean difference that looked exactly like a
loader bug.** The mirror was simply on.

Rendering only says "different". **Diffing the buffers says which one** — `PlyDump.Run`
writes the five files plus `meta.json`. On luigi:

```
sh.bin          identical
scale           identical, max difference 0
colour + alpha  identical, rgb delta 0.000000
positions       the same set, permuted (the converter reorders spatially)
rotation        0.0748° mean error against the source ply; the converter's is 0.0993°
```

`PlyDump.Run` writes `meta.json` too, so **the loader doubles as an offline converter** —
useful when you want a converted directory without Unity 2022 or Python.
