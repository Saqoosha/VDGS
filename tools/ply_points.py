#!/usr/bin/env python3
"""Strip a 3DGS ply down to bare points, for tools that only want positions.

    python3 tools/ply_points.py in.ply out.ply [--stride N]

OpenVDB's vdb_tool reads a point cloud and rasterises it into a narrow-band level set.
It has no use for spherical harmonics, opacity or covariance, and a 3DGS ply carries 236
bytes per splat of exactly that. Positions alone are 12, so this is also what makes the
file small enough to push over the network without thinking about it.

Ordinary little-endian binary ply, so anything reads it.
"""
import argparse
import re

import numpy as np


def read_positions(path):
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
    dtype = np.dtype([(n, sizes[k]) for k, n in re.findall(r'property (\w+) (\w+)', text)])
    rows = np.memmap(path, dtype=dtype, mode='r', offset=end, shape=(count,))
    return np.stack([rows['x'], rows['y'], rows['z']], 1).astype(np.float32)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('input')
    ap.add_argument('output')
    ap.add_argument('--stride', type=int, default=1,
                    help='keep every Nth point; the cloud is far denser than any voxel grid')
    args = ap.parse_args()

    xyz = read_positions(args.input)
    if args.stride > 1:
        xyz = xyz[::args.stride]

    header = (
        'ply\n'
        'format binary_little_endian 1.0\n'
        f'element vertex {len(xyz)}\n'
        'property float x\n'
        'property float y\n'
        'property float z\n'
        'end_header\n'
    ).encode('ascii')

    with open(args.output, 'wb') as f:
        f.write(header)
        f.write(np.ascontiguousarray(xyz, dtype='<f4').tobytes())

    print(f'   {len(xyz):,} points   {len(xyz) * 12 / 1e6:.1f} MB   '
          f'bounds {np.round(xyz.min(0), 2)} .. {np.round(xyz.max(0), 2)}')


if __name__ == '__main__':
    main()
