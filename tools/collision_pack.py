#!/usr/bin/env python3
"""Pack a collision mesh and the per-part colliders into the files the runtime reads.

    python3 tools/collision_pack.py --mesh ground.ply --parts all.parts.json out/ [--mirror-y]

Writes out/collision.bin (the mesh, format of glb_to_collision.py) and out/parts.json
(capsules and boxes, format of fit_parts.py). Either input may be left out.

FRAME: both inputs are in the capture's ply frame (right-handed, Y-down), which is what
fit_parts.py and ground_heightfield.py produce. The runtime mirrors a `.ply` capture's
collision files itself, so for a `.ply` leave --mirror-y off. A converted directory is NOT
mirrored at runtime, so its files must arrive already mirrored: pass --mirror-y. The flag
does the same three things the runtime does - negates Y, swaps the triangle winding (a
reflection turns the faces inside out), and re-rights the box frames. Getting this wrong
produces no error: the ground is a ceiling 2 m overhead and the gates hang upside down.
"""
import argparse, json, os, re, struct
import numpy as np


def read_mesh_ply(path):
    r = open(path, 'rb').read(); end = r.index(b'end_header') + 11; t = r[:end].decode()
    nv = int(re.search(r'element vertex (\d+)', t).group(1))
    nf = int(re.search(r'element face (\d+)', t).group(1))
    props = re.findall(r'property (\w+) (\w+)', t.split('element face')[0])
    assert [p[1] for p in props[:3]] == ['x', 'y', 'z'] and len(props) == 3, 'mesh ply must carry x y z only'
    V = np.frombuffer(r, '<f4', nv * 3, end).reshape(-1, 3).copy()
    F = np.frombuffer(r, np.dtype([('c', 'u1'), ('i', '<i4', 3)]), nf, end + nv * 12)['i'].copy()
    return V, F


def write_collision_bin(path, V, F):
    with open(path, 'wb') as f:
        f.write(struct.pack('<III', 1, len(V), F.size))
        f.write(V.astype('<f4').tobytes()); f.write(F.astype('<u4').tobytes())


def mirror_parts(parts):
    for c in parts.get('capsules', []):
        c['a'][1] = -c['a'][1]; c['b'][1] = -c['b'][1]
    for b in parts.get('boxes', []):
        b['c'][1] = -b['c'][1]
        u = np.array(b['u']) * [1, -1, 1]; v = np.array(b['v']) * [1, -1, 1]
        # a reflection makes (u, v, w) left-handed; a box is symmetric in w, so take w = u x v
        b['u'] = u.round(5).tolist(); b['v'] = v.round(5).tolist(); b['w'] = np.cross(u, v).round(5).tolist()
    parts['frame'] = 'unity'
    return parts


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('out')
    ap.add_argument('--mesh', help='mesh ply (x y z only) -> collision.bin')
    ap.add_argument('--parts', help='parts json from fit_parts.py -> parts.json')
    ap.add_argument('--mirror-y', action='store_true')
    a = ap.parse_args()
    if not a.mesh and not a.parts:
        raise SystemExit('nothing to pack: pass --mesh and/or --parts')
    os.makedirs(a.out, exist_ok=True)
    if a.mesh:
        V, F = read_mesh_ply(a.mesh)
        if not np.isfinite(V).all():
            raise SystemExit('mesh has non-finite vertices')
        if a.mirror_y:
            V[:, 1] *= -1; F = F[:, [0, 2, 1]]
        write_collision_bin(os.path.join(a.out, 'collision.bin'), V, F)
        print(f'collision.bin: {len(V):,} verts, {len(F):,} tris{" (mirrored, winding swapped)" if a.mirror_y else ""}')
    if a.parts:
        parts = json.load(open(a.parts))
        for c in parts.get('capsules', []):
            for k in list(c):
                if k.startswith('_'): del c[k]
        if a.mirror_y:
            parts = mirror_parts(parts)
        for b in parts.get('boxes', []):
            u, v, w = (np.array(b[k]) for k in 'uvw')
            if abs(np.dot(np.cross(u, v), w) - 1) > 1e-3:
                raise SystemExit('box frame is not right-handed and orthonormal')
        json.dump(parts, open(os.path.join(a.out, 'parts.json'), 'w'), indent=1)
        print(f'parts.json: {len(parts.get("capsules", []))} capsules, {len(parts.get("boxes", []))} boxes'
              f'{" (mirrored)" if a.mirror_y else ""}')


if __name__ == '__main__':
    main()
