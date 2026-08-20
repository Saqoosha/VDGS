#!/usr/bin/env python3
"""Build a collision mesh from a splat cloud through a CONTINUOUS density field.

    python3 tools/gs_field_mesh.py in.ply out.glb [--res 0.06] [--iso 0.35] [--smooth 1.0]

WHY NOT THE VOXEL PIPELINE

splat-transform decides each voxel is solid or empty by thresholding accumulated opacity,
and meshes the boundary of that binary volume. A 3DGS floor is built from wide, flat
gaussians whose density near the floor plane hovers around whatever threshold you pick, so
neighbouring columns disagree and the surface steps a whole voxel at a time. Measured on
drjohnson at voxel 0.12: the median height difference between neighbouring floor cells is
0.120 - exactly one voxel - with 70% of neighbour pairs a voxel or more apart. That is a
12 cm staircase across the entire floor, and a drone catches on it.

Switching splat-transform to `--collision-mesh smooth` does not fix it (0.120 -> 0.120,
70% -> 67%), and it cannot: marching cubes interpolates between field values, and between
0 and 1 the crossing is always the midpoint. Sub-voxel placement needs a field that is
continuous BEFORE it is meshed.

So evaluate the gaussian mixture itself on the grid - each gaussian contributes
alpha * exp(-0.5 * d^T Sigma^-1 d) - and take an isosurface of that. The crossing point
then lands between grid points, in proportion to the real density, and a flat floor comes
out flat regardless of how it lines up with the grid.

This is the standard post-hoc route in the literature (build a density grid, marching
cubes an iso-level) and is what Gaussian Opacity Fields formalises. The mature engineering
version of the same idea is OpenVDB: rasterise particles into a narrow-band level set,
run LevelSetFilter over it, then dual-contour. If this script's numbers justify it, that
is the next step up - its documentation says outright that raw particle rasterisation is
"too noisy and blobby" without the filtering stage, which is our problem exactly.

REQUIRES scipy and scikit-image, which the project does not otherwise use.
"""
import argparse
import json
import re
import struct
import sys

import numpy as np

GLB_MAGIC = 0x46546C67
CHUNK_JSON = 0x4E4F534A
CHUNK_BIN = 0x004E4942


def read_ply(path):
    """Positions, world-space covariance factors and opacity for every gaussian."""
    with open(path, 'rb') as f:
        head = b''
        while b'end_header' not in head:
            chunk = f.read(65536)
            if not chunk:
                raise SystemExit(f'{path}: no end_header')
            head += chunk
    end = head.index(b'end_header') + len(b'end_header\n')
    text = head[:end].decode('ascii', 'replace')
    count = int(re.search(r'element vertex (\d+)', text).group(1))
    sizes = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4', 'short': '<i2'}
    props = re.findall(r'property (\w+) (\w+)', text)
    dtype = np.dtype([(name, sizes[kind]) for kind, name in props])
    rows = np.memmap(path, dtype=dtype, mode='r', offset=end, shape=(count,))

    mu = np.stack([rows['x'], rows['y'], rows['z']], 1).astype(np.float32)
    # 3DGS stores log scale and a logit opacity.
    scale = np.exp(np.stack([rows[f'scale_{i}'] for i in range(3)], 1).astype(np.float32))
    quat = np.stack([rows[f'rot_{i}'] for i in range(4)], 1).astype(np.float32)
    alpha = 1.0 / (1.0 + np.exp(-rows['opacity'].astype(np.float32)))

    # Real captures ship unnormalised quaternions - the synthetic test data does not,
    # which is how that goes unnoticed until it reaches a real scene.
    quat /= np.maximum(np.linalg.norm(quat, axis=1, keepdims=True), 1e-12)
    return mu, scale, quat, alpha


def rotations(quat):
    """(w, x, y, z) -> 3x3 rotation matrices."""
    w, x, y, z = quat[:, 0], quat[:, 1], quat[:, 2], quat[:, 3]
    R = np.empty((len(quat), 3, 3), np.float32)
    R[:, 0, 0] = 1 - 2 * (y * y + z * z)
    R[:, 0, 1] = 2 * (x * y - w * z)
    R[:, 0, 2] = 2 * (x * z + w * y)
    R[:, 1, 0] = 2 * (x * y + w * z)
    R[:, 1, 1] = 1 - 2 * (x * x + z * z)
    R[:, 1, 2] = 2 * (y * z - w * x)
    R[:, 2, 0] = 2 * (x * z - w * y)
    R[:, 2, 1] = 2 * (y * z + w * x)
    R[:, 2, 2] = 1 - 2 * (x * x + y * y)
    return R


def accumulate(field, origin, res, mu, scale, quat, alpha, sigmas, chatty=True):
    """Add every gaussian's density into the grid.

    Gaussians are grouped by how many cells they span so each group can be splatted with
    one vectorised stencil. Almost all of them cover a single cell - a capture is mostly
    small gaussians - so the expensive wide stencils run on a tiny minority.
    """
    R = rotations(quat)
    # Sigma = R diag(s^2) R^T; only its diagonal is needed to size the footprint.
    cov_diag = np.einsum('nij,nj,nij->ni', R, scale.astype(np.float32) ** 2, R)
    reach = sigmas * np.sqrt(np.maximum(cov_diag, 1e-12))
    half = np.ceil(reach / res).astype(np.int32)
    width = half.max(axis=1)

    inv_s2 = (1.0 / np.maximum(scale.astype(np.float32) ** 2, 1e-12))
    shape = np.array(field.shape)
    cut = 0.5 * sigmas * sigmas

    for w in np.unique(width):
        idx = np.flatnonzero(width == w)
        span = 2 * int(w) + 1
        stencil = span ** 3
        # Cap the working set rather than the scene: a chunk is bounded by cells, so a
        # group of very wide gaussians simply runs in more, smaller passes.
        per_chunk = max(1, int(4e6 // stencil))
        if chatty:
            print(f'   splat  half-width {w:>3}  {len(idx):>10,} gaussians  '
                  f'stencil {span}^3', flush=True)

        offs = np.stack(np.meshgrid(*[np.arange(-w, w + 1)] * 3, indexing='ij'), -1)
        offs = offs.reshape(-1, 3).astype(np.int32)

        for start in range(0, len(idx), per_chunk):
            g = idx[start:start + per_chunk]
            base = np.floor((mu[g] - origin) / res).astype(np.int32)
            cells = base[:, None, :] + offs[None, :, :]            # (n, stencil, 3)

            inside = np.all((cells >= 0) & (cells < shape), axis=2)
            pos = origin + (cells.astype(np.float32) + 0.5) * res
            d = pos - mu[g][:, None, :]
            # Into each gaussian's own frame, where the covariance is diagonal.
            local = np.einsum('nij,nkj->nki', R[g].transpose(0, 2, 1), d)
            m = 0.5 * np.sum(local * local * inv_s2[g][:, None, :], axis=2)

            keep = inside & (m < cut)
            if not keep.any():
                continue
            weight = alpha[g][:, None] * np.exp(-m)
            flat = np.ravel_multi_index(
                np.clip(cells, 0, shape - 1).reshape(-1, 3).T, field.shape)
            np.add.at(field.reshape(-1), flat[keep.reshape(-1)], weight[keep])


def write_glb(path, verts, tris):
    verts = np.ascontiguousarray(verts, dtype='<f4')
    tris = np.ascontiguousarray(tris, dtype='<u4').reshape(-1)
    blob = verts.tobytes() + tris.tobytes()
    blob += b'\0' * (-len(blob) % 4)
    gltf = {
        'asset': {'version': '2.0', 'generator': 'vdgs gs_field_mesh'},
        'scene': 0, 'scenes': [{'nodes': [0]}], 'nodes': [{'mesh': 0}],
        'meshes': [{'primitives': [{'attributes': {'POSITION': 0}, 'indices': 1}]}],
        'accessors': [
            {'bufferView': 0, 'componentType': 5126, 'count': len(verts), 'type': 'VEC3',
             'min': verts.min(0).tolist(), 'max': verts.max(0).tolist()},
            {'bufferView': 1, 'componentType': 5125, 'count': len(tris), 'type': 'SCALAR'},
        ],
        'bufferViews': [
            {'buffer': 0, 'byteOffset': 0, 'byteLength': verts.nbytes},
            {'buffer': 0, 'byteOffset': verts.nbytes, 'byteLength': tris.nbytes},
        ],
        'buffers': [{'byteLength': len(blob)}],
    }
    head = json.dumps(gltf, separators=(',', ':')).encode('utf8')
    head += b' ' * (-len(head) % 4)
    with open(path, 'wb') as f:
        f.write(struct.pack('<III', GLB_MAGIC, 2, 12 + 8 + len(head) + 8 + len(blob)))
        f.write(struct.pack('<II', len(head), CHUNK_JSON))
        f.write(head)
        f.write(struct.pack('<II', len(blob), CHUNK_BIN))
        f.write(blob)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('input')
    ap.add_argument('output')
    ap.add_argument('--res', type=float, default=0.06, help='grid spacing in world units')
    ap.add_argument('--iso', type=float, default=0.35, help='isosurface level of the density field')
    ap.add_argument('--smooth', type=float, default=1.0, help='gaussian blur of the field, in cells')
    ap.add_argument('--sigmas', type=float, default=2.5, help='how far each gaussian is splatted')
    ap.add_argument('--margin', type=float, default=0.5, help='padding around the cloud, in world units')
    args = ap.parse_args()

    from scipy.ndimage import gaussian_filter
    from skimage.measure import marching_cubes

    mu, scale, quat, alpha = read_ply(args.input)
    print(f'   loaded     {len(mu):,} gaussians')

    lo = mu.min(0) - args.margin
    hi = mu.max(0) + args.margin
    dims = np.ceil((hi - lo) / args.res).astype(int) + 1
    cells = int(np.prod(dims))
    print(f'   grid       {dims.tolist()}  =  {cells:,} cells  ({cells * 4 / 1e6:.0f} MB)')
    if cells > 400_000_000:
        raise SystemExit('grid too large - raise --res')

    field = np.zeros(tuple(dims), np.float32)
    accumulate(field, lo.astype(np.float32), args.res, mu, scale, quat, alpha, args.sigmas)
    print(f'   density    max {field.max():.2f}  mean {field.mean():.4f}  '
          f'cells over iso {int((field > args.iso).sum()):,}')

    if args.smooth > 0:
        field = gaussian_filter(field, args.smooth, mode='constant')
        print(f'   smoothed   sigma {args.smooth} cells, max now {field.max():.2f}')

    if field.max() <= args.iso:
        raise SystemExit(f'nothing reaches iso {args.iso} - lower it')

    verts, faces, _normals, _vals = marching_cubes(field, level=args.iso, spacing=(args.res,) * 3)
    verts = verts + lo
    print(f'   mesh       {len(faces):,} tris  {len(verts):,} verts')

    write_glb(args.output, verts, faces)
    print(f'   wrote      {args.output}')


if __name__ == '__main__':
    main()
