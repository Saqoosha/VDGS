#!/usr/bin/env python3
"""Numbers that let two captures of different fields be compared: splat health, ground
coverage over the flown area, and how much of a low viewpoint's view is big splats.

    python3 tools/scene_metrics.py scene.ply [--band 3] [--cell 1] [--fly 1.5]

Input is a ply already in metres with +Y up (the frame the game loads). The ground is
local: per 2 m XZ cell, the 10th-percentile Y of small opaque splats, diffused into empty
cells, so slopes and ditches do not shift the band (docs/cleanup.ja.md, `y=0` is not the
ground). Coverage is area, not count: a cell's coverage is the sum over band splats of
pi * a * b * alpha (two largest axes) divided by the cell area, capped at 4.

Only numpy: it has to run on the Mac and inside a bare WSL venv alike.
"""
import argparse

import numpy as np

SZ = {"float": "<f4", "double": "<f8", "uchar": "u1", "char": "i1",
      "int": "<i4", "uint": "<u4", "short": "<i2", "ushort": "<u2"}


def read_ply(path):
    with open(path, "rb") as f:
        head = b""
        while b"end_header" not in head:
            head += f.read(1 << 12)
        end = head.index(b"end_header\n") + len(b"end_header\n")
        lines = head[:end].decode().splitlines()
        n = int(next(l for l in lines if l.startswith("element vertex")).split()[2])
        props = [(l.split()[2], SZ[l.split()[1]]) for l in lines if l.startswith("property")]
        f.seek(end)
        return np.frombuffer(f.read(n * np.dtype(props).itemsize), dtype=np.dtype(props))


def local_ground(xyz, small, gcell):
    """XZ grid of ground height from small opaque splats, holes filled by diffusion."""
    x0, z0 = xyz[:, 0].min(), xyz[:, 2].min()
    nx = int((xyz[:, 0].max() - x0) / gcell) + 1
    nz = int((xyz[:, 2].max() - z0) / gcell) + 1
    ix = ((xyz[small, 0] - x0) / gcell).astype(int)
    iz = ((xyz[small, 2] - z0) / gcell).astype(int)
    key = ix * nz + iz
    order = np.argsort(key)
    key, ys = key[order], xyz[small, 1][order]
    starts = np.r_[0, np.flatnonzero(np.diff(key)) + 1]
    g = np.full(nx * nz, np.nan)
    for s, e in zip(starts, np.r_[starts[1:], len(key)]):
        if e - s >= 5:
            g[key[s]] = np.percentile(ys[s:e], 10)
    g = g.reshape(nx, nz)
    known = ~np.isnan(g)
    for _ in range(200):                       # diffuse into empty cells
        if known.all():
            break
        p = np.pad(g, 1, mode="edge")
        nb = np.stack([p[:-2, 1:-1], p[2:, 1:-1], p[1:-1, :-2], p[1:-1, 2:]])
        fill = np.nanmean(nb, axis=0)
        g = np.where(known, g, fill)
        known = ~np.isnan(g)
    return g, x0, z0, nx, nz, known


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ply")
    ap.add_argument("--band", type=float, default=3.0, help="m above local ground")
    ap.add_argument("--cell", type=float, default=1.0, help="coverage cell, m")
    ap.add_argument("--gcell", type=float, default=2.0, help="ground grid, m")
    ap.add_argument("--fly", type=float, default=1.5, help="viewpoint height for fog share, m")
    ap.add_argument("--big", type=float, default=1.0, help="'big splat' long axis, m")
    ap.add_argument("--radius", type=float, default=None,
                    help="only cells within this many m (XZ) of the hard-splat centre: the flown area")
    a = ap.parse_args()

    v = read_ply(a.ply)
    xyz = np.c_[v["x"], v["y"], v["z"]].astype(np.float64)
    op = 1 / (1 + np.exp(-v["opacity"].astype(np.float64)))
    sc = np.exp(np.c_[v["scale_0"], v["scale_1"], v["scale_2"]].astype(np.float64))
    sc.sort(axis=1)
    n = len(v)
    print(f"{a.ply}: {n:,} splats")
    print(f"  opacity p50 {np.median(op):.3f}   hard(>0.5) {np.mean(op > 0.5) * 100:.1f}%")
    print(f"  long axis m: p50 {np.median(sc[:, 2]):.3f}  p90 {np.percentile(sc[:, 2], 90):.3f}"
          f"  p99 {np.percentile(sc[:, 2], 99):.3f}  >{a.big}m: {np.sum(sc[:, 2] > a.big):,}")

    small = (sc[:, 2] < 0.15) & (op > 0.5)
    g, x0, z0, nx, nz, known = local_ground(xyz, small, a.gcell)
    gi = np.clip(((xyz[:, 0] - x0) / a.gcell).astype(int), 0, nx - 1)
    gk = np.clip(((xyz[:, 2] - z0) / a.gcell).astype(int), 0, nz - 1)
    h = xyz[:, 1] - g[gi, gk]                       # height above local ground
    band = (h > -0.5) & (h < a.band)
    print(f"  ground grid {nx}x{nz} @ {a.gcell}m, height range {np.nanmin(g):.1f}..{np.nanmax(g):.1f} m;"
          f"  splats in band: {band.sum():,} ({band.mean() * 100:.1f}%)")

    # coverage per cell, over the cells that have any hard splat within the band: the
    # flown/captured area, so an empty edge of the bounding box does not count as holes
    cx = ((xyz[band, 0] - x0) / a.cell).astype(int)
    cz = ((xyz[band, 2] - z0) / a.cell).astype(int)
    ncx = int((xyz[:, 0].max() - x0) / a.cell) + 1
    ncz = int((xyz[:, 2].max() - z0) / a.cell) + 1
    area = np.pi * sc[band, 2] * sc[band, 1] * op[band]
    cnt = np.bincount(cx * ncz + cz, weights=(op[band] > 0.5), minlength=ncx * ncz)
    occupied = cnt >= 3
    if a.radius:
        ctr = np.median(xyz[band & (op > 0.5)][:, [0, 2]], axis=0)
        ccx = x0 + (np.arange(ncx * ncz) // ncz + 0.5) * a.cell
        ccz = z0 + (np.arange(ncx * ncz) % ncz + 0.5) * a.cell
        occupied &= np.hypot(ccx - ctr[0], ccz - ctr[1]) < a.radius
    # three readings of the same cells: everything, long axes clamped at `big` (what
    # capsize.py would leave), and small splats only (what the photos actually resolved)
    capped = np.pi * np.minimum(sc[band, 2], a.big) * np.minimum(sc[band, 1], a.big) * op[band]
    smallw = area * (sc[band, 2] < 0.5)
    for label, w in (("all", area), (f"capped@{a.big}m", capped), ("small<0.5m", smallw)):
        cov = np.bincount(cx * ncz + cz, weights=w, minlength=ncx * ncz) / (a.cell ** 2)
        c = np.minimum(cov[occupied], 4.0)
        print(f"  coverage[{label:12s}] over {occupied.sum():,} cells: p10 {np.percentile(c, 10):.2f}"
              f"  p50 {np.median(c):.2f}  p90 {np.percentile(c, 90):.2f}   <1.0: {np.mean(c < 1) * 100:.1f}%"
              f"   <0.25: {np.mean(c < 0.25) * 100:.1f}%")

    # big splats near the ground, and their share of the band's area
    big = band & (sc[:, 2] > a.big)
    print(f"  big (>{a.big}m) splats in band: {big.sum():,}  = {np.pi * (sc[big, 2] * sc[big, 1] * op[big]).sum() / max(area.sum(), 1e-9) * 100:.1f}% of band area")

    # fog share: from viewpoints at `fly` m over the flown cells, solid-angle weight
    # (r/d)^2 * alpha summed over splats within 60 m; share taken by splats > big
    rng = np.random.default_rng(0)
    cells = np.flatnonzero(occupied)
    pick = rng.choice(cells, min(200, len(cells)), replace=False)
    px = x0 + (pick // ncz + 0.5) * a.cell
    pz = z0 + (pick % ncz + 0.5) * a.cell
    py = g[np.clip(((px - x0) / a.gcell).astype(int), 0, nx - 1),
           np.clip(((pz - z0) / a.gcell).astype(int), 0, nz - 1)] + a.fly
    tot = bigshare = 0.0
    hard = op > 0.05
    P = xyz[hard]; r = sc[hard, 2]; al = op[hard]; isbig = sc[hard, 2] > a.big
    for i in range(len(pick)):
        d = np.linalg.norm(P - np.array([px[i], py[i], pz[i]]), axis=1)
        near = d < 60
        w = (r[near] / np.maximum(d[near], 0.3)) ** 2 * al[near]
        tot += w.sum(); bigshare += w[isbig[near]].sum()
    print(f"  from {len(pick)} viewpoints at {a.fly}m: big-splat share of view weight {bigshare / max(tot, 1e-9) * 100:.1f}%")


if __name__ == "__main__":
    main()
