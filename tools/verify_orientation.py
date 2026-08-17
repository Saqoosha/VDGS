#!/usr/bin/env python3
"""Check that every splat's orientation survived the conversion to Unity's format.

A wrong quaternion does not look wrong. It renders as a slightly hazier version
of the same scene, and the only symptom - spiky highlights - shows up late, in
the sim, after a slow round trip. That is exactly how the mirror bug hid: the
positions were perfect while every ellipsoid pointed somewhere else.

So compare numerically instead. For each splat this reconstructs the ellipsoid
frame on both sides and measures the angle between corresponding axes:

    source .ply  ->  quaternion (w,x,y,z) + log scales
    other.bin    ->  10.10.10.2 "smallest three" quaternion + scales

Two details make this non-obvious, and getting either wrong produces a confident
wrong answer:

  * The converter reorders splats spatially, so index i on one side is not
    index i on the other. Splats are matched by position, not by index.
  * The decoded float4 is (x, y, z, w). Reading it as (w, x, y, z) - the order
    the .ply uses - yields ~38 degrees of average error and looks like a real bug.

Expected result is not zero: rotations are always packed to 10 bits per
component regardless of quality setting, which puts a hard floor at about
0.18 degrees. Anything past ~1 degree is a genuine defect.

    python3 tools/verify_orientation.py <source.ply> <converted-dir> [--mirror y]

`--mirror` applies the same reflection the conversion pipeline does, so the
production path can be checked end to end.
"""

import argparse
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from align_ply import read_ply  # noqa: E402

# Rotation is packed to 32 bits whatever the quality level; only scale varies.
SCALE_BYTES = {"Float32": 12, "Norm16": 6, "Norm11": 4, "Norm6": 2}


def decode_rotations(other, stride):
    """Unpack the 10.10.10.2 'smallest three' quaternions -> (N, 4) as (w,x,y,z)."""
    packed = np.ascontiguousarray(other[:, :4]).view(np.uint32).ravel()

    p = np.stack([(packed >> s) & 1023 for s in (0, 10, 20)], -1) / 1023.0
    largest = ((packed >> 30) & 3).astype(int)

    # The three stored components were remapped from [-1/sqrt2, 1/sqrt2] to [0,1];
    # the dropped one is recovered from the unit-length constraint.
    xyz = p * np.sqrt(2.0) - (1.0 / np.sqrt(2.0))
    w = np.sqrt(np.clip(1.0 - (xyz ** 2).sum(-1), 0.0, 1.0))

    # Reinsert the dropped component at its original slot, giving Unity's (x,y,z,w).
    n = len(packed)
    q = np.empty((n, 4))
    for slot in range(4):
        m = largest == slot
        if not m.any():
            continue
        rest = xyz[m]
        parts = [rest[:, 0], rest[:, 1], rest[:, 2]]
        parts.insert(slot, w[m])
        q[m] = np.stack(parts, -1)

    return q[:, [3, 0, 1, 2]]  # (x,y,z,w) -> (w,x,y,z)


def rot_matrices(q):
    """(N,4) quaternions as (w,x,y,z) -> (N,3,3); columns are the ellipsoid axes."""
    w, x, y, z = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    return np.stack([
        np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)], -1),
        np.stack([2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)], -1),
        np.stack([2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)], -1),
    ], -2)


def load_source(path, mirror):
    _, props, rows = read_ply(path)
    pos = rows[:, [props.index(c) for c in "xyz"]].astype(np.float64)
    q = rows[:, [props.index(f"rot_{i}") for i in range(4)]].astype(np.float64)
    scale = np.exp(rows[:, [props.index(f"scale_{i}") for i in range(3)]].astype(np.float64))

    # Training never constrains the quaternion, so real captures store unnormalised
    # ones - bonsai's sit around 0.98. Every renderer normalises on load, and the
    # matrix formula below is only a rotation for a unit quaternion: feeding it a
    # 0.98-length one yields a skewed matrix and tens of degrees of phantom error.
    norm = np.linalg.norm(q, axis=1, keepdims=True)
    q = q / np.where(norm == 0.0, 1.0, norm)

    if mirror:
        axis = "xyz".index(mirror)
        pos[:, axis] *= -1.0
        # Negate the two quaternion components other than the mirrored axis;
        # this is the quaternion form of R -> M R M.
        for k in range(3):
            if k != axis:
                q[:, 1 + k] *= -1.0

    return pos, q, scale


def load_converted(directory):
    with open(os.path.join(directory, "meta.json")) as f:
        meta = json.load(f)

    n = meta["splatCount"]
    if meta["posFormat"] != "Float32":
        raise SystemExit(f"posFormat is {meta['posFormat']}; only Float32 can be matched "
                         "by position (re-convert with -vdgsQuality VeryHigh)")

    stride = 4 + SCALE_BYTES[meta["scaleFormat"]]
    # Both buffers are padded up to a 16-byte boundary; trim the tail.
    other = np.fromfile(os.path.join(directory, "other.bin"), dtype=np.uint8)
    if other.size < n * stride:
        raise SystemExit(f"other.bin is {other.size} bytes, too short for "
                         f"{n} splats x {stride}")
    other = other[:n * stride].reshape(n, stride)

    pos = np.fromfile(os.path.join(directory, "pos.bin"), dtype=np.float32)
    if pos.size < n * 3:
        raise SystemExit(f"pos.bin holds {pos.size} floats, too short for {n} splats")
    pos = pos[:n * 3].reshape(n, 3).astype(np.float64)

    q = decode_rotations(other, stride)
    if meta["scaleFormat"] != "Float32":
        raise SystemExit(f"scaleFormat is {meta['scaleFormat']}; only Float32 is decoded here")
    scale = np.ascontiguousarray(other[:, 4:16]).view(np.float32).reshape(n, 3).astype(np.float64)

    return pos, q, scale, meta


def match_by_position(dst, src, cell):
    """Map each converted splat to its source splat. The converter reorders spatially.

    A voxel hash rather than a KD-tree: it needs no scipy, runs in one pass over
    millions of splats, and the points are near-identical rather than merely
    close, so only the 27 neighbouring cells ever need looking at.
    """
    keys = np.floor(src / cell).astype(np.int64)
    table = {}
    for i, k in enumerate(map(tuple, keys)):
        table.setdefault(k, []).append(i)

    idx = np.full(len(dst), -1, dtype=np.int64)
    dist = np.full(len(dst), np.inf)
    offsets = [(a, b, c) for a in (-1, 0, 1) for b in (-1, 0, 1) for c in (-1, 0, 1)]

    dkeys = np.floor(dst / cell).astype(np.int64)
    for i in range(len(dst)):
        kx, ky, kz = dkeys[i]
        best, bestd = -1, np.inf
        for ox, oy, oz in offsets:
            for j in table.get((kx + ox, ky + oy, kz + oz), ()):
                d = ((src[j] - dst[i]) ** 2).sum()
                if d < bestd:
                    best, bestd = j, d
        idx[i], dist[i] = best, np.sqrt(bestd)

    if (idx < 0).any():
        raise SystemExit(f"{(idx < 0).sum():,} splats had no source within one cell "
                         f"({cell}) - try a larger --cell")
    return idx, dist


def align_translation(dst, src):
    """The pipeline re-grounds the floor after mirroring, so undo that shift.

    Only a translation: the reflection is exact and the scale is left at 1, so
    the centroids differ by a constant.
    """
    return dst.mean(0) - src.mean(0)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ply")
    ap.add_argument("converted")
    ap.add_argument("--mirror", choices=["x", "y", "z"],
                    help="reflection the pipeline applied before converting")
    ap.add_argument("--sample", type=int, default=200000,
                    help="check at most this many splats (default 200000)")
    ap.add_argument("--tolerance", type=float, default=1.0,
                    help="fail above this mean error in degrees (default 1.0)")
    ap.add_argument("--cell", type=float, default=0.05,
                    help="voxel size used to match splats by position (default 0.05)")
    args = ap.parse_args()

    s_pos, s_q, s_scale = load_source(args.ply, args.mirror)
    d_pos, d_q, d_scale, meta = load_converted(args.converted)

    print(f"source    : {len(s_pos):,} splats  {args.ply}")
    print(f"converted : {len(d_pos):,} splats  {args.converted}")
    if args.mirror:
        print(f"mirror    : {args.mirror} (applied to the source before comparing)")
    if len(s_pos) != len(d_pos):
        print(f"!! splat counts differ - the conversion dropped "
              f"{len(s_pos) - len(d_pos):,}")

    # Before sampling: a centroid taken from a subsample would not line up with
    # the full source, and the resulting bogus offset mismatches splats in dense
    # regions, which then reads as an orientation bug.
    shift = align_translation(d_pos, s_pos)
    if np.abs(shift).max() > 1e-6:
        print(f"offset    : {np.round(shift, 4)} (the pipeline re-grounds the floor)")

    if len(d_pos) > args.sample:
        pick = np.random.default_rng(0).choice(len(d_pos), args.sample, replace=False)
        d_pos, d_q, d_scale = d_pos[pick], d_q[pick], d_scale[pick]
        print(f"sampling  : {args.sample:,} splats")

    idx, dist = match_by_position(d_pos - shift, s_pos, args.cell)
    print(f"matching  : max position gap {dist.max():.2e}")
    if dist.max() > 1e-3:
        print("!! positions do not line up - the source and the conversion "
              "are not the same scene")

    s_q, s_scale = s_q[idx], s_scale[idx]

    Rs, Rd = rot_matrices(s_q), rot_matrices(d_q)
    # Compare corresponding ellipsoid axes. abs() because an axis has no
    # head or tail - the 'smallest three' packing is free to flip a sign.
    dots = np.abs(np.einsum("nji,nji->ni", Rs, Rd))
    ang = np.degrees(np.arccos(np.clip(dots, -1.0, 1.0)))

    # An axis of a near-spherical splat has no meaningful direction, so it
    # would report large errors that mean nothing. Weight by how elongated it is.
    elong = s_scale.max(1) / np.maximum(s_scale.min(1), 1e-12)
    directional = elong > 1.5

    worst = ang.max(1)
    print()
    print(f"orientation error over {len(worst):,} splats")
    print(f"  mean {worst.mean():7.3f}   median {np.median(worst):7.3f}   "
          f"p99 {np.percentile(worst, 99):7.3f}   max {worst.max():7.3f}  deg")
    if directional.any():
        w = worst[directional]
        print(f"  elongated only ({directional.sum():,}): mean {w.mean():7.3f}   "
              f"p99 {np.percentile(w, 99):7.3f}   max {w.max():7.3f}  deg")

    scale_ok = np.allclose(d_scale, s_scale, rtol=1e-3, atol=1e-5)
    print(f"  scales preserved: {scale_ok}")

    ref = worst[directional] if directional.any() else worst
    # 10-bit packing puts an unavoidable floor at roughly 0.18 deg.
    print()
    if ref.mean() <= args.tolerance and scale_ok:
        print(f"PASS - within the {args.tolerance} deg budget "
              "(10-bit rotation packing alone costs ~0.18 deg)")
        return 0
    print(f"FAIL - mean {ref.mean():.3f} deg exceeds {args.tolerance} deg")
    print("  ~38 deg means the quaternion component order is being read wrong;")
    print("  ~90 deg means axes are swapped; a few deg means a bad reflection.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
