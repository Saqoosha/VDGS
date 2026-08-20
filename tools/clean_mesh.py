#!/usr/bin/env python3
"""Drop floating debris from a collision mesh, then decimate it to a minimum feature size.

    python3 clean_mesh.py in.ply out.ply --voxel 0.04 \
        [--min-voxels 100] [--min-extent 0.25] [--min-edge 0.05] [--max-tris N]

TWO PROBLEMS, BOTH SEEN BEFORE THEY WERE MEASURED.

Islands. A capture has floaters, and rasterising them leaves small closed blobs sitting in
mid-air with nothing around them. In the game those are invisible walls. They are also
trivially separable: a blob is its own connected component, while a real surface belongs
to the one big component that is the room. Dropped when it is small by volume AND by reach - either one being substantial keeps
the component. See drop_islands for why neither test works on its own.

Feature size. Generating at a fine voxel and then decimating to a triangle COUNT leaves
dense regions carrying triangles far smaller than the drone. They cannot change where the
drone can fly; they only add surface detail for the solver to resolve. So the target here
is a LENGTH: reduce until the median edge reaches the smallest feature worth representing.
Triangle count then falls out of the scene instead of being imposed on it.

Edge length goes as 1/sqrt(triangle count) on a fixed surface, which turns the target into
a direct estimate rather than a search.
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from decimate_mesh import read_mesh, write_mesh          # noqa: E402


def edge_lengths(verts, tris):
    v = verts.astype(np.float64)
    return np.concatenate([
        np.linalg.norm(v[tris[:, 0]] - v[tris[:, 1]], axis=1),
        np.linalg.norm(v[tris[:, 1]] - v[tris[:, 2]], axis=1),
        np.linalg.norm(v[tris[:, 2]] - v[tris[:, 0]], axis=1),
    ])


def drop_islands(verts, tris, min_volume, min_extent):
    """Remove connected components that are small by BOTH volume and reach.

    Neither test works alone. Volume separates hardest - it goes as the cube of the
    length, so a blob and a real object land orders apart rather than a factor of two -
    but a handrail, a pipe or a chair leg is long and encloses almost nothing, and a
    volume-only rule deletes them. Reach alone keeps any blob that happens to be spread
    out. A component survives if it is substantial in either sense; only something small
    in both is rasterisation noise.

    The level set is closed, so each component's enclosed volume is the divergence-theorem
    sum over its triangles. Sign depends on winding, so take the magnitude.
    """
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components

    e0 = np.concatenate([tris[:, 0], tris[:, 1], tris[:, 2]])
    e1 = np.concatenate([tris[:, 1], tris[:, 2], tris[:, 0]])
    graph = coo_matrix((np.ones(len(e0), np.int8), (e0, e1)),
                       shape=(len(verts), len(verts)))
    ncomp, label = connected_components(graph, directed=False)

    v = verts.astype(np.float64)
    a, b, c = v[tris[:, 0]], v[tris[:, 1]], v[tris[:, 2]]
    signed = np.einsum('ij,ij->i', a, np.cross(b, c)) / 6.0
    vol = np.abs(np.bincount(label[tris[:, 0]], weights=signed, minlength=ncomp))

    lo = np.full((ncomp, 3), np.inf)
    hi = np.full((ncomp, 3), -np.inf)
    np.minimum.at(lo, label, v)
    np.maximum.at(hi, label, v)
    diag = np.linalg.norm(hi - lo, axis=1)

    keep_component = (vol >= min_volume) | (diag >= min_extent)
    keep = keep_component[label[tris[:, 0]]]
    kept = tris[keep]
    if len(kept) == 0:
        raise SystemExit(f'nothing reaches {min_volume} m3 or {min_extent} m')

    used = np.unique(kept)
    remap = np.zeros(len(verts), np.int32)
    remap[used] = np.arange(len(used), dtype=np.int32)

    gone = ~keep_component
    dropped = int(gone.sum())
    print(f'   islands  {ncomp:,} components, dropped {dropped:,} '
          f'(under {min_volume:.5f} m3 AND {min_extent} m)  '
          f'{len(tris) - len(kept):,} tris')
    if dropped:
        print(f'            largest dropped: {vol[gone].max():.5f} m3, '
              f'{diag[gone].max():.3f} m   kept by reach alone: '
              f'{int(((vol < min_volume) & (diag >= min_extent)).sum()):,}')
    return verts[used], remap[kept]


def decimate_to_edge(verts, tris, target_edge, max_tris, rounds=4):
    import fast_simplification

    for _ in range(rounds):
        med = float(np.median(edge_lengths(verts, tris)))
        too_many = max_tris is not None and len(tris) > max_tris
        if med >= target_edge and not too_many:
            break

        want = len(tris) * (med / target_edge) ** 2 if med < target_edge else len(tris)
        if max_tris is not None:
            want = min(want, max_tris)
        want = int(max(want, 64))
        if want >= len(tris):
            break

        verts, tris = fast_simplification.simplify(verts, tris, 1.0 - want / len(tris))
        verts = np.ascontiguousarray(verts, np.float32)
        tris = np.ascontiguousarray(tris, np.int32)

    return verts, tris


def report(tag, verts, tris):
    el = edge_lengths(verts, tris)
    print(f'   {tag:8} {len(verts):>9,} verts {len(tris):>9,} tris   '
          f'edge median {np.median(el):.3f}  p10 {np.percentile(el, 10):.3f}  '
          f'p90 {np.percentile(el, 90):.3f}', flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('input')
    ap.add_argument('output')
    ap.add_argument('--voxel', type=float, required=True,
                    help='the voxel size the mesh was generated at, in metres')
    ap.add_argument('--min-voxels', type=float, default=100,
                    help='a component needs this many voxel volumes, or --min-extent, to stay')
    ap.add_argument('--min-extent', type=float, default=0.25,
                    help='bounding diagonal that keeps a component regardless of volume, '
                         'in metres - this is what saves rails, pipes and chair legs')
    ap.add_argument('--min-edge', type=float, default=0.05,
                    help='decimate until the median edge reaches this, in metres')
    ap.add_argument('--max-tris', type=int, default=None,
                    help='hard cap, applied on top of the edge target')
    args = ap.parse_args()

    verts, tris = read_mesh(args.input)
    report('in', verts, tris)

    lo, hi = int(tris.min()), int(tris.max())
    if lo < 0 or hi >= len(verts):
        raise SystemExit(f'{args.input}: face indices out of range [{lo}, {hi}]')

    # The threshold is counted in voxels, not cubic metres. A blob a few voxels across is
    # rasterisation noise whatever the scene's scale, so one number carries across
    # captures and voxel sizes instead of being retuned per scene.
    if args.min_voxels > 0 or args.min_extent > 0:
        verts, tris = drop_islands(verts, tris,
                                   args.min_voxels * args.voxel ** 3, args.min_extent)
        report('islands', verts, tris)

    verts, tris = decimate_to_edge(verts, tris, args.min_edge, args.max_tris)
    report('out', verts, tris)
    write_mesh(args.output, verts, tris)


if __name__ == '__main__':
    main()
