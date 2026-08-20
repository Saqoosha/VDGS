#!/usr/bin/env python3
"""Put a splat-transform collision mesh into Unity's frame, and drop its boundary box.

    python3 tools/trim_collision.py in.glb out.glb

TWO THINGS HAPPEN HERE, AND THE FIRST ONE IS NOT COSMETIC.

1. Frame. splat-transform emits its collision mesh rotated 180 degrees about Z relative
   to the ply it was given - (x, y, z) becomes (-x, -y, z). The capture reaches Unity
   through `align_ply.py --mirror y`, which is (x, -y, z). The two differ by a negated
   X, so a mesh used as-is would be a mirror image of the room it is supposed to
   enclose. Nothing about that looks wrong: the shell still hugs the capture, the
   bounding boxes still match to a voxel, and the walls are still where walls should be.
   It is the same failure mode this project has already paid a day for.

   Undoing it is a negated X, which reverses handedness, so the triangle winding is
   reversed with it. `m_QueriesHitBackfaces` is false in this game, so an
   inverted mesh goes invisible to raycasts - and it is NOT solid to contacts either.
   PhysX treats a non-convex triangle mesh as single-sided: a body arriving from behind
   passes through and gets held from the far side. Measured, see the design spec.

   Measured rather than read: the mesh AABB was compared against the source cloud under
   all four sign combinations. (-x, -y, z) lands within one voxel on every face; the
   next best candidate is nine voxels out. Do not take this from the .voxel.json header
   instead - its bounds are in a DIFFERENT frame from the glb vertices, and reading them
   is what sent this the wrong way the first time.

2. The boundary box. Exterior flood fill marks everything outside the capture as solid,
   so the mesh carries the room's inner faces plus the six sides of the voxel grid. The
   grid sides are the one surface a drone can never reach - the solid region between the
   room and the grid edge is exactly what stops it - and they hide the whole scene from
   any viewpoint outside, which is what made the first preview unreadable. Greedy
   meshing merges each side into two triangles, so this removes about 12 of them; the
   win is legibility, not triangle count.

   Detection is by construction rather than by heuristic: those faces lie on the six
   planes of the mesh's own bounding volume, so a triangle belongs to the box exactly
   when all three of its vertices sit on one of those planes. Nothing inside the room
   can touch them, because the fill guarantees a solid voxel in between.
"""
import json
import struct
import sys

import numpy as np

GLB_MAGIC = 0x46546C67
CHUNK_JSON = 0x4E4F534A
CHUNK_BIN = 0x004E4942


def read_glb(path):
    data = open(path, 'rb').read()
    magic, _version, total = struct.unpack_from('<III', data, 0)
    if magic != GLB_MAGIC:
        raise SystemExit(f'{path}: not a glb')

    offset, chunks = 12, []
    while offset < total:
        length, kind = struct.unpack_from('<II', data, offset)
        offset += 8
        chunks.append((kind, data[offset:offset + length]))
        offset += length

    gltf = json.loads(chunks[0][1].decode('utf8'))
    blob = chunks[1][1]

    def accessor(index):
        acc = gltf['accessors'][index]
        view = gltf['bufferViews'][acc['bufferView']]
        start = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
        kind = {5126: '<f4', 5125: '<u4', 5123: '<u2'}[acc['componentType']]
        width = 3 if acc['type'] == 'VEC3' else 1
        flat = np.frombuffer(blob, dtype=kind, count=acc['count'] * width, offset=start)
        return flat.reshape(-1, width) if width > 1 else flat

    prim = gltf['meshes'][0]['primitives'][0]
    return accessor(prim['attributes']['POSITION']), accessor(prim['indices'])


def write_glb(path, verts, indices):
    verts = np.ascontiguousarray(verts, dtype='<f4')
    indices = np.ascontiguousarray(indices, dtype='<u4')
    blob = verts.tobytes() + indices.tobytes()
    blob += b'\0' * (-len(blob) % 4)

    gltf = {
        'asset': {'version': '2.0', 'generator': 'vdgs trim_collision'},
        'scene': 0,
        'scenes': [{'nodes': [0]}],
        'nodes': [{'mesh': 0}],
        'meshes': [{'primitives': [{'attributes': {'POSITION': 0}, 'indices': 1}]}],
        'accessors': [
            {'bufferView': 0, 'componentType': 5126, 'count': len(verts), 'type': 'VEC3',
             'min': verts.min(0).tolist(), 'max': verts.max(0).tolist()},
            {'bufferView': 1, 'componentType': 5125, 'count': len(indices), 'type': 'SCALAR'},
        ],
        'bufferViews': [
            {'buffer': 0, 'byteOffset': 0, 'byteLength': verts.nbytes},
            {'buffer': 0, 'byteOffset': verts.nbytes, 'byteLength': indices.nbytes},
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
    src, dst = sys.argv[1:3]
    verts, indices = read_glb(src)
    tris = indices.reshape(-1, 3)

    lo, hi = verts.min(0), verts.max(0)
    # Voxel-quantised coordinates land exactly on the plane; the tolerance is for
    # float32 rounding and costs nothing.
    eps = np.maximum((hi - lo) * 1e-5, 1e-6)
    on_low = np.abs(verts - lo) <= eps
    on_high = np.abs(verts - hi) <= eps

    boundary = np.zeros(len(tris), dtype=bool)
    for plane in (on_low, on_high):
        for axis in range(3):
            boundary |= plane[tris, axis].all(axis=1)

    kept = tris[~boundary]
    if len(kept) == 0:
        raise SystemExit(f'{src}: every triangle is on the boundary - the shell is empty')

    used = np.unique(kept)
    remap = np.zeros(len(verts), dtype=np.uint32)
    remap[used] = np.arange(len(used), dtype=np.uint32)
    out_verts = verts[used].copy()
    out_tris = remap[kept]

    # Into Unity's frame. See the module docstring for why X and not something else.
    out_verts[:, 0] *= -1
    # A negated axis flips handedness, so every triangle now faces inward. Swapping two
    # indices per triangle turns them back out.
    out_tris = out_tris[:, ::-1]

    write_glb(dst, out_verts, out_tris.reshape(-1))

    print(f'   trimmed    {len(tris) - len(kept):,} boundary tris of {len(tris):,}'
          f'  ->  {len(kept):,} kept, {len(used):,} verts, X negated for Unity')


if __name__ == '__main__':
    main()
