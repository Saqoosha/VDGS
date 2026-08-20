#!/usr/bin/env python3
"""Measure a capture and its collision shell, and write what the preview page prints.

The numbers are the point. Every orientation bug this project has had - the mirrored
scene, the stale chunk buffer, the orthographic camera - produced a picture that looked
like a plausible capture, so the preview states the two bounding boxes and their ratio
rather than leaving the reader to judge alignment by eye.

Coverage is collision extent over splat extent, per axis. The external fill wraps the
whole capture, so a shell that does not enclose it has leaked; 0.9 is the threshold.
"""
import json
import os
import re
import struct
import sys

import numpy as np


def ply_positions(path):
    with open(path, 'rb') as f:
        head = b''
        while b'end_header' not in head:
            chunk = f.read(4096)
            if not chunk:
                raise SystemExit(f'{path}: no end_header')
            head += chunk
    end = head.index(b'end_header') + len(b'end_header\n')
    text = head[:end].decode('ascii', 'replace')
    count = int(re.search(r'element vertex (\d+)', text).group(1))
    sizes = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4', 'short': '<i2'}
    dtype = np.dtype([(name, sizes[kind]) for kind, name in re.findall(r'property (\w+) (\w+)', text)])
    rows = np.memmap(path, dtype=dtype, mode='r', offset=end, shape=(count,))
    return count, np.stack([rows['x'], rows['y'], rows['z']], 1).astype(np.float64)


def glb_mesh_bounds(path):
    data = open(path, 'rb').read()
    _, _, total = struct.unpack_from('<III', data, 0)
    offset, chunks = 12, []
    while offset < total:
        length, _kind = struct.unpack_from('<II', data, offset)
        offset += 8
        chunks.append(data[offset:offset + length])
        offset += length
    gltf = json.loads(chunks[0].decode('utf8'))
    prim = gltf['meshes'][0]['primitives'][0]
    pos = gltf['accessors'][prim['attributes']['POSITION']]
    tris = gltf['accessors'][prim['indices']]['count'] // 3
    return np.array(pos['min'], float), np.array(pos['max'], float), tris


def main():
    name, voxel, ply_path, glb_path, out_path = sys.argv[1:6]
    sog = os.path.join(os.path.dirname(out_path), 'viewer.sog')

    splats, xyz = ply_positions(ply_path)
    # No Y negation. vdb_tool builds the mesh in the source ply's own frame, and the
    # capture is previewed in that frame too, so the two already agree. An earlier
    # version negated Y here and in the mesh writer, which rendered playroom upside
    # down - correct relative to itself, wrong relative to the room.
    s_min, s_max = xyz.min(0), xyz.max(0)
    c_min, c_max, tris = glb_mesh_bounds(glb_path)

    # Coverage is measured against a ROBUST extent, not the raw bounding box.
    #
    # The raw box is set by whichever splat is furthest out, so a handful of strays
    # decides the verdict. drjohnson failed the first version of this check at 0.87 on
    # Z, and the whole deficit was NINE splats sitting three units past the shell -
    # the room itself was enclosed correctly. A gate that cries wolf on nine splats out
    # of three million teaches you to ignore it, which is worse than not having it.
    p_lo, p_hi = np.percentile(xyz, [0.5, 99.5], axis=0)
    span = np.maximum(p_hi - p_lo, 1e-6)
    ratio = (c_max - c_min) / span

    # The honest second number: how much of the capture is actually inside the shell's
    # box. Cheap, and it does not care where the outliers are.
    inside = np.all((xyz >= c_min) & (xyz <= c_max), axis=1).mean()

    meta = {
        'name': name,
        'voxel': float(voxel),
        'splats': int(splats),
        'triangles': int(tris),
        # Version the asset URLs by mtime. A page reload re-reads the HTML but the engine
        # fetches meshes over XHR, which keeps using the HTTP cache - so a regenerated
        # mesh silently renders as the previous one. That looked exactly like a broken
        # coordinate transform: the shell sat below the capture, flipped in Y, while the
        # file on disk was correct.
        'files': {
            'splat': f'viewer.sog?v={int(os.path.getmtime(sog)) if os.path.exists(sog) else 0}',
            'collision': f'collision.glb?v={int(os.path.getmtime(glb_path))}',
        },
        # center/extent frame the camera and must describe the WHOLE cloud. The robust
        # extent is for the coverage ratio only - on a scene with a long tail it is a
        # third of the real size, which parks the camera inside the shell.
        'splat': {
            'min': s_min.tolist(), 'max': s_max.tolist(),
            'center': ((s_min + s_max) / 2).tolist(), 'extent': (s_max - s_min).tolist(),
            'robustExtent': span.tolist(),
        },
        'collision': {'min': c_min.tolist(), 'max': c_max.tolist()},
        'ratio': ratio.tolist(),
        'enclosed': float(inside),
    }
    with open(out_path, 'w') as f:
        json.dump(meta, f, indent=1)

    per_splat = tris / max(splats, 1)
    worst = ratio.min()
    print(f'   splats     {splats:,}')
    print(f'   triangles  {tris:,}   {per_splat:.3f} per splat'
          + ('   WARNING: capture looks spongy' if per_splat > 0.3 else ''))
    print(f'   coverage   {np.round(ratio, 2)}   ' + ('FAIL - shell leaked' if worst < 0.9 else 'ok'))
    print(f'   enclosed   {100 * inside:.2f}% of splats inside the shell box')


if __name__ == '__main__':
    main()
