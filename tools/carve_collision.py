#!/usr/bin/env python3
"""Seal a collision mesh from a seed: whatever the drone cannot reach becomes solid.

    uv run -q --python 3.12 --with numpy --with scipy --with scikit-image \
        --with fast-simplification python tools/carve_collision.py \
        in.{bin,ply} out.{bin,ply} (--mode sealed|open | --seed x,y,z) \
        [--radius 0.08] [--grid R/2]

Measured 2026-09-27 by dropping balls straight down in PhysX from points in the flying
space (CollisionTest.RunPoints): the shipped FDF, JDL-R5 and textilni meshes let 191-194 of
200 balls through the surface they land on, to rest 0.22-0.34 m lower on the underside of
the ground slab. Their winding had been chosen by a drop test that aimed with one-sided
raycasts, which cannot see the faces it was judging. Carved, 193-197 of 200 hold. Not
confirmed in the game: the drone is not a ball, and on these blurry grounds 0.2 m is not
visible from the cockpit.

The idea is PlayCanvas splat-transform's `--voxel-carve` (src/lib/voxel/carve.ts): flood a
body from a seed through free space, keep what it reaches, invert. Their version works on a
binary voxel grid, so its surface is stepped to the voxel. This one keeps the OpenVDB
surface where the drone can touch it, and only seals the rest.

WHY. "Which side is the flying side" cannot be read off a mesh - the design note spent
three drop-test designs finding that out, and `--reverse` is still chosen per scene by
hand. A seed answers it by definition: the flying side is what the seed can reach. Three
things follow from that one definition:

  winding     every output triangle faces the reachable side, by construction. PhysX
              meshes are single-sided, so this is what makes the floor hold from above.
  thickness   everything unreachable is solid, so walls are as thick as whatever is
              behind them, not 4 x voxel. Tunnelling no longer depends on the voxel.
  debris      floaters behind walls or under the ground are inside the solid and emit
              no triangles. Holes narrower than 2 x radius are sealed.

HOW. On a grid of spacing `grid`:

  D      unsigned distance to the input surface (EDT, refined near the surface with a
         KD-tree over points sampled on the triangles)
  R      cells with D > radius that are connected to the seed: where the drone's centre
         can be
  s      radius - (distance to R). Positive within radius of a reachable centre - the
         space the drone's body sweeps. Where the drone touches a surface, s equals D, so
         the zero level lands on the input surface; where it cannot fit, the zero level
         seals the gap at radius from the nearest reachable centre.

The zero level of s is extracted with marching cubes. The distance to R is corrected for
R's boundary lying between cell centres (R's true edge is D = radius, the nearest cell
centre is up to one cell inside it), which is what keeps touched surfaces on the input
surface rather than up to a cell in front of it.

REQUIREMENTS. The input must be closed (the OpenVDB level set is): the flood relies on the
surface to keep it out of the inside of the walls. `radius >= grid` so the flood cannot
step across a surface between two cells.

THE SEED has no default, because the obvious ones are wrong. The largest free region
indoors is the padding outside the walls, and choosing it turns the room solid. The region
touching the most surface is also the outside (playroom: 297 m^2 against 175 m^2). And the
inside of every wall is a free region of its own, since the level set's walls are slabs
thicker than 2 x radius. So: --mode sealed takes the largest region that does not reach the
grid's edge and is thicker than 4 x radius (walls are ~radius), and refuses when there is
none - the room leaks. --mode open takes the largest region that reaches the edge: outdoor
scenes, and every indoor capture measured so far (playroom and drjohnson both leak through
their ceilings).

FRAME. Unchanged - the output sits in the input's frame, so a `.ply` capture's mesh stays
pre-mirror. SplatCollision mirrors and flips winding together, which preserves which side
each triangle faces. No `--reverse` on glb_to_collision afterwards.
"""
import argparse
import os
import struct
import sys
import time

import numpy as np
from scipy import ndimage
from scipy.spatial import cKDTree
from skimage.measure import marching_cubes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from decimate_mesh import read_mesh, write_mesh          # noqa: E402

MAX_CELLS = 400_000_000


def read_any(path):
    if path.endswith('.bin'):
        with open(path, 'rb') as f:
            version, nv, ni = struct.unpack('<III', f.read(12))
            if version != 1:
                raise SystemExit(f'{path}: collision.bin version {version}, expected 1')
            verts = np.frombuffer(f.read(nv * 12), '<f4').reshape(-1, 3)
            idx = np.frombuffer(f.read(ni * 4), '<u4').reshape(-1, 3)
        return verts.astype(np.float64), idx.astype(np.int64)
    verts, tris = read_mesh(path)
    return np.asarray(verts, np.float64), np.asarray(tris, np.int64).reshape(-1, 3)


def write_any(path, verts, tris):
    if path.endswith('.bin'):
        v = np.ascontiguousarray(verts, '<f4')
        i = np.ascontiguousarray(tris.reshape(-1), '<u4')
        with open(path, 'wb') as f:
            f.write(struct.pack('<III', 1, len(v), len(i)))
            f.write(v.tobytes())
            f.write(i.tobytes())
    else:
        write_mesh(path, verts, tris)


def sample_surface(verts, tris, spacing):
    """Points on every triangle, no two further apart than `spacing` along an edge."""
    a, b, c = verts[tris[:, 0]], verts[tris[:, 1]], verts[tris[:, 2]]
    longest = np.maximum.reduce([np.linalg.norm(b - a, axis=1),
                                 np.linalg.norm(c - b, axis=1),
                                 np.linalg.norm(a - c, axis=1)])
    k_all = np.clip(np.ceil(longest / spacing), 1, 64).astype(np.int64)
    out = [verts]
    for k in np.unique(k_all):
        sel = k_all == k
        i, j = np.meshgrid(np.arange(k + 1), np.arange(k + 1), indexing='ij')
        keep = (i + j) <= k
        u = (i[keep] / k)[None, :, None]
        w = (j[keep] / k)[None, :, None]
        pa, pb, pc = a[sel][:, None], b[sel][:, None], c[sel][:, None]
        out.append((pa + u * (pb - pa) + w * (pc - pa)).reshape(-1, 3))
    return np.concatenate(out)


def write_drop_points(path, s, R, lo, g, down, count=200):
    """Where CollisionTest.RunPoints should drop a ball: straight down from a reachable
    cell to the first surface, kept only where that surface is flat enough to rest on.

    Written in the frame the game builds the collider in, so for a pre-mirror mesh
    (down = +1 cell in y) the points are mirrored here to match SplatCollision.
    """
    rng = np.random.default_rng(0)
    cells = np.argwhere(R)
    cells = cells[rng.permutation(len(cells))[:count * 50]]
    hi = np.array(s.shape) - 2
    out = []
    for i, j, k in cells:
        col = s[i, j::down, k]
        below = np.nonzero(col <= 0)[0]
        if len(below) == 0 or below[0] * g > 5:
            continue
        m = below[0]
        t = col[m - 1] / (col[m - 1] - col[m])            # zero crossing between m-1 and m
        jj = j + down * (m - 1 + t)
        a, b, c = np.clip([i, int(round(jj)), k], 1, hi)
        n = np.array([s[a + 1, b, c] - s[a - 1, b, c], s[a, b + 1, c] - s[a, b - 1, c],
                      s[a, b, c + 1] - s[a, b, c - 1]])
        if abs(n[1]) < 0.8 * np.linalg.norm(n):
            continue
        y = lo[1] + jj * g
        out.append((lo[0] + i * g, y if down < 0 else -y, lo[2] + k * g))
        if len(out) == count:
            break
    np.savetxt(path, np.array(out), fmt='%.4f')
    print(f'   drop points  {len(out)} -> {path}')


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('src')
    ap.add_argument('dst')
    ap.add_argument('--radius', type=float, default=0.08,
                    help='radius of the body flooded from the seed, metres (default 0.08)')
    ap.add_argument('--grid', type=float, default=None,
                    help='cell size, metres (default radius / 2)')
    how = ap.add_mutually_exclusive_group(required=True)
    how.add_argument('--mode', choices=('sealed', 'open'),
                     help='sealed: the flying space is closed (a room that holds) - '
                          'everything outside it becomes solid. open: it reaches the edge '
                          'of the grid (outdoors, or a room with holes) - fixes winding and '
                          'fills what is unreachable, the outside stays open')
    how.add_argument('--seed', help='x,y,z in the mesh frame, inside the flying space')
    ap.add_argument('--drop-points', metavar='TXT',
                    help='also write 200 "x ytop z" lines for CollisionTest.RunPoints: '
                         'flat surfaces seen straight down from the flying space')
    ap.add_argument('--up', choices=('+y', '-y'), default='+y',
                    help='which way is up in this file. --up=-y for a .ply capture\'s mesh, which '
                         'is pre-mirror; its drop points are then written mirrored, the way '
                         'the game loads the mesh')
    args = ap.parse_args()

    r = args.radius
    g = args.grid or r / 2
    if r < g:
        raise SystemExit(f'--radius {r} < --grid {g}: the flood could step across a surface')

    t0 = time.time()
    verts, tris = read_any(args.src)
    print(f'   input   {len(verts):,} verts  {len(tris):,} tris')

    pad = r + 3 * g
    lo = verts.min(0) - pad
    shape = tuple(int(n) for n in np.ceil((verts.max(0) + pad - lo) / g).astype(int) + 1)
    cells = int(np.prod(shape))
    print(f'   grid    {shape} = {cells / 1e6:.1f}M cells at {g} m, radius {r} m')
    if cells > MAX_CELLS:
        raise SystemExit(f'{cells:,} cells is over {MAX_CELLS:,}: pass a coarser --grid')

    # D: distance to the surface. Coarse everywhere from an EDT of the cells holding
    # surface samples, exact (to the sample spacing) in the band where it decides anything.
    pts = sample_surface(verts, tris, g / 3)
    ijk = np.clip(np.rint((pts - lo) / g).astype(np.int64), 0, np.array(shape) - 1)
    occ = np.zeros(shape, bool)
    occ[ijk[:, 0], ijk[:, 1], ijk[:, 2]] = True
    D = ndimage.distance_transform_edt(~occ, sampling=g).astype(np.float32)
    band = np.nonzero(D < r + 3 * g)
    centres = lo + np.stack(band, 1) * g
    D[band] = cKDTree(pts).query(centres, workers=-1)[0]
    del occ, pts, centres
    print(f'   distance field  {time.time() - t0:.1f} s  ({len(band[0]):,} band cells)')

    # R: where the drone's centre can be, connected to the seed.
    free = D > r
    labels, n = ndimage.label(free)
    sizes = np.bincount(labels.ravel(), minlength=n + 1)
    sizes[0] = 0
    skin = np.bincount(labels[free & (D <= r + 1.5 * g)], minlength=n + 1)
    # Volume over skin area is half a region's typical thickness. It tells a room from the
    # inside of a wall: the level set's walls are slabs thicker than 2 x radius, so their
    # insides are free regions too - on playroom 11 m^3 wrapped in 175 m^2 of skin, 6 cm.
    thick = sizes * g / np.maximum(skin, 1)
    edge = np.zeros(n + 1, bool)
    for axis in range(3):
        for end in (0, -1):
            edge[np.unique(np.take(labels, end, axis=axis))] = True
    edge[0] = False
    order = np.argsort(sizes)[::-1][:4]
    for rank, lab in enumerate(order):
        if sizes[lab] == 0:
            break
        where = np.argwhere(labels == lab)
        print(f'   region {rank}  {sizes[lab] * g ** 3:9.2f} m^3  thickness {thick[lab]:6.2f} m  '
              f'{"open  " if edge[lab] else "sealed"}  '
              f'{np.round(lo + where.min(0) * g, 2)} .. {np.round(lo + where.max(0) * g, 2)}')
    if args.seed:
        seed = np.array([float(v) for v in args.seed.split(',')])
        cell = np.clip(np.rint((seed - lo) / g).astype(int), 0, np.array(shape) - 1)
        if not free[tuple(cell)]:
            # Inside or too close to geometry: take the nearest cell the body fits in.
            cand = np.argwhere(free)
            cell = cand[np.argmin(((cand - cell) ** 2).sum(1))]
            print(f'   seed was not free, moved to {np.round(lo + cell * g, 3)}')
        chosen = labels[tuple(cell)]
    else:
        # No default. The largest region is the wrong answer for a sealed room - it is the
        # padding outside the walls, and picking it turns the room itself solid.
        ok = (sizes > 0) & (edge if args.mode == 'open' else ~edge & (thick > 4 * r))
        if not ok.any():
            raise SystemExit(f'no {args.mode} region'
                             + (' thicker than 4 x radius: the room leaks to the outside, '
                                'use --mode open' if args.mode == 'sealed' else ''))
        chosen = int(np.argmax(np.where(ok, sizes, 0)))
    if sizes[chosen] == 0:
        raise SystemExit('no free space: the radius does not fit anywhere')
    print(f'   seed region  {sizes[chosen] * g ** 3:.2f} m^3 of {n} regions')
    R = labels == chosen
    del labels, free

    # s: signed, positive where the drone's body can sweep.
    dist, idx = ndimage.distance_transform_edt(~R, sampling=g, return_indices=True)
    # R's edge is D = r, and the nearest cell centre in R sits D - r inside it.
    inside_by = D[idx[0], idx[1], idx[2]] - r
    del idx
    s = (r - np.maximum(dist - inside_by, 0.0)).astype(np.float32)
    s[R] = r
    del dist, inside_by, D
    print(f'   carve  {time.time() - t0:.1f} s')
    if args.drop_points:
        write_drop_points(args.drop_points, s, R, lo, g, -1 if args.up == '+y' else 1)
    del R

    mv, mf, _, _ = marching_cubes(s, level=0.0, spacing=(g, g, g))
    mv = mv + lo
    mf = mf.astype(np.int64)

    # Face the reachable side: n = cross(b - a, c - a) is what PhysX takes as the front.
    a, b, c = mv[mf[:, 0]], mv[mf[:, 1]], mv[mf[:, 2]]
    nrm = np.cross(b - a, c - a)
    nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-12)
    probe = ((a + b + c) / 3 + nrm * (g / 2) - lo) / g
    ahead = ndimage.map_coordinates(s, probe.T, order=1, mode='nearest')
    facing = float((ahead > 0).mean())
    if facing < 0.5:
        mf = mf[:, ::-1]
        facing = 1 - facing
    print(f'   facing the reachable side: {facing:.4f} of triangles')

    write_any(args.dst, mv, mf)
    print(f'   output  {len(mv):,} verts  {len(mf):,} tris  ({time.time() - t0:.1f} s)')
    print(f'   wrote {args.dst}')


if __name__ == '__main__':
    main()
