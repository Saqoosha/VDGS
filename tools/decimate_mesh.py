#!/usr/bin/env python3
"""Triangulate a vdb_tool mesh and decimate it to a target triangle count.

    python3 tools/decimate_mesh.py in.ply out.ply TARGET_TRIS

The point of decimating instead of using a coarser voxel: the gap between the collision
surface and the splats is about 2 x voxel, while the triangle count goes as 1 / voxel^2.
Generating fine and reducing afterwards should buy the small gap at the coarse triangle
count - if the decimator moves the surface less than the voxel it replaces. That is
measurable, so it does not have to be assumed either way.

Quadric edge collapse minimises squared distance to the original planes, which is exactly
the quantity the gap measures.
"""
import re
import struct
import sys

import numpy as np
import fast_simplification

SIZES = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'char': 'i1',
         'int': '<i4', 'uint': '<u4', 'short': '<i2', 'ushort': '<u2'}


def read_mesh(path):
    with open(path, 'rb') as f:
        head = b''
        while b'end_header' not in head:
            c = f.read(1 << 16)
            if not c:
                raise SystemExit(f'{path}: no end_header')
            head += c
    end = head.index(b'end_header') + len(b'end_header\n')
    text = head[:end].decode('ascii', 'replace')
    nvert = int(re.search(r'element vertex (\d+)', text).group(1))
    nface = int(re.search(r'element face (\d+)', text).group(1))

    vsec = text.split('element vertex')[1].split('element face')[0]
    vdt = np.dtype([(n, SIZES[k]) for k, n in re.findall(r'property (\w+) (\w+)', vsec)])
    fsec = text.split('element face')[1]
    m = re.search(r'property list (\w+) (\w+) (\w+)', fsec)
    cnt_t, idx_t = SIZES[m.group(1)], SIZES[m.group(2)]

    with open(path, 'rb') as f:
        f.seek(end)
        verts = np.frombuffer(f.read(nvert * vdt.itemsize), dtype=vdt, count=nvert)
        blob = f.read()

    xyz = np.stack([verts['x'], verts['y'], verts['z']], 1).astype(np.float32)

    # Faces are a variable-length list and adaptive meshing emits a mix of triangles and
    # quads, so the records have no fixed stride and cannot be read as one array. The
    # sizes still have to be walked one at a time, but reading the indices does not:
    # gather the bytes for a whole size-group at once and view them as int32. A per-face
    # inner loop takes minutes on a three-million-face mesh; this is under a second.
    csz, isz = np.dtype(cnt_t).itemsize, np.dtype(idx_t).itemsize
    raw = np.frombuffer(blob, dtype=np.uint8)

    offs = np.empty(nface, np.int64)
    sizes = np.empty(nface, np.int32)
    off = 0
    for i in range(nface):
        # int() is not decoration. raw[off] is a numpy uint8, and under numpy 2's
        # promotion rules `python_int += uint8` yields uint8, so the offset wraps at 256
        # and every index past the first few faces is garbage. The symptom was a
        # segfault inside the decimator, nowhere near here.
        n = int(raw[off])
        off += csz
        offs[i] = off
        sizes[i] = n
        off += n * isz

    tris = []
    for n in np.unique(sizes):
        if n < 3:
            continue
        sel = np.flatnonzero(sizes == n)
        span = offs[sel][:, None] + np.arange(n * isz)[None, :]
        idx = np.ascontiguousarray(raw[span]).view(idx_t).reshape(len(sel), n)
        for k in range(1, n - 1):
            tris.append(np.stack([idx[:, 0], idx[:, k], idx[:, k + 1]], 1))
    return xyz, np.concatenate(tris).astype(np.int32)


def write_mesh(path, verts, tris):
    verts = np.ascontiguousarray(verts, dtype='<f4')
    tris = np.ascontiguousarray(tris, dtype='<i4')
    header = (
        'ply\nformat binary_little_endian 1.0\n'
        f'element vertex {len(verts)}\n'
        'property float x\nproperty float y\nproperty float z\n'
        f'element face {len(tris)}\n'
        'property list uchar int vertex_indices\n'
        'end_header\n'
    ).encode('ascii')
    counts = np.full((len(tris), 1), 3, np.uint8)
    with open(path, 'wb') as f:
        f.write(header)
        f.write(verts.tobytes())
        # uchar count then three int32 per face, interleaved.
        rec = np.empty(len(tris), dtype=[('n', 'u1'), ('i', '<i4', 3)])
        rec['n'] = 3
        rec['i'] = tris
        f.write(rec.tobytes())


def main():
    src, dst, target = sys.argv[1], sys.argv[2], int(sys.argv[3])
    verts, tris = read_mesh(src)
    print(f'   in    {len(verts):>9,} verts  {len(tris):>9,} tris', flush=True)

    # The decimator is C and takes indices on trust, so a parsing bug reaches it as a
    # segfault with no line number. Check here instead.
    lo, hi = int(tris.min()), int(tris.max())
    if lo < 0 or hi >= len(verts):
        raise SystemExit(f'{src}: face indices out of range [{lo}, {hi}] for '
                         f'{len(verts)} verts - the ply parse is wrong')

    if len(tris) <= target:
        write_mesh(dst, verts, tris)
        print(f'   out   already under target, copied')
        return

    reduction = 1.0 - target / len(tris)
    v2, t2 = fast_simplification.simplify(verts, tris, reduction)
    write_mesh(dst, v2, t2)
    print(f'   out   {len(v2):>9,} verts  {len(t2):>9,} tris  '
          f'(reduction {reduction:.3f})')


if __name__ == '__main__':
    main()
