#!/usr/bin/env python3
"""Thin a splat scene to a target count while keeping the objects at the given points.

Near a protected point, a splat stays if it is not grass-coloured, has opacity over 0.4, a largest axis under 0.6 m, and sits
0.05-6 m above the ground. The ground around it is treated like all other ground, because keeping it whole leaves a sharp disc
around every object. The rest are ranked by what they contribute to the picture, opacity x the area of their two largest axes,
weighted down with distance from the course line beyond --near metres, and the top ones are kept until the count reaches
--target. A far tree loses more than the grass under the line. Splats with non-finite values are always dropped.

The ply must be Y-up: a raw Y-down capture protects nothing, because every object is then below its ground point.

    python3 tools/thin_ply.py in.ply out.ply --objects objects.json --target 3000000

objects.json is {"keep": [[x, z, ground y], ...], "line": [[x, y, z], ...]} in the ply's own frame.
"""
import argparse
import json

import numpy as np
from plyfile import PlyData, PlyElement
from scipy.spatial import cKDTree


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--objects", required=True)
    ap.add_argument("--target", type=int, default=3_000_000)
    ap.add_argument("--keep-radius", type=float, default=2.6)
    ap.add_argument("--near", type=float, default=15.0)
    a = ap.parse_args()

    ply = PlyData.read(a.src)
    v = ply["vertex"].data
    v = v[np.all([np.isfinite(v[n]) for n in v.dtype.names], axis=0)]
    o = json.load(open(a.objects))
    xz = np.c_[v["x"], v["z"]].astype(np.float64)
    op = 1 / (1 + np.exp(-v["opacity"].astype(np.float64)))
    s = np.sort(np.exp(np.c_[v["scale_0"], v["scale_1"], v["scale_2"]].astype(np.float64)), axis=1)

    sites = np.array(o["keep"], float)
    d_keep, near = cKDTree(sites[:, :2]).query(xz, distance_upper_bound=a.keep_radius)
    close = np.isfinite(d_keep)
    h = np.full(len(v), -1e9)
    h[close] = v["y"][close] - sites[near[close], 2]
    r, g, b = (np.asarray(v[f"f_dc_{i}"], np.float64) * 0.28209479177387814 + 0.5 for i in range(3))
    grass = (r > 1.25 * b) & (g > 1.15 * b) & (abs(r - g) < 0.16) & (r + g + b > 0.55)
    keep = close & ~grass & (op > 0.4) & (s[:, 2] < 0.6) & (h > 0.05) & (h < 6)
    print(f"{len(v):,} splats, {keep.sum():,} protected", flush=True)

    line = np.array(o["line"], float)[:, [0, 2]]
    d_line, _ = cKDTree(line).query(xz)
    score = op * s[:, 2] * s[:, 1] * np.minimum(1.0, (a.near / np.maximum(d_line, 1e-3)) ** 2)

    room = a.target - int(keep.sum())
    if room < 0:
        raise SystemExit(f"the protected splats alone are over the target ({keep.sum():,})")
    rest = np.flatnonzero(~keep)
    keep[rest[np.argpartition(-score[rest], room)[:room]] if room < len(rest) else rest] = True
    lost = score[~keep].sum() / score.sum()
    print(f"kept {keep.sum():,}, dropped {(~keep).sum():,}, {lost:.1%} of the weighted coverage went with them", flush=True)
    for lo, hi in [(0, 15), (15, 40), (40, 1e9)]:
        m = (d_line >= lo) & (d_line < hi)
        print(f"  {lo:g}-{hi:g} m from the line: {keep[m].sum():,} of {m.sum():,}", flush=True)
    PlyData([PlyElement.describe(v[keep], "vertex")], text=False).write(a.out)


if __name__ == "__main__":
    main()
