#!/usr/bin/env python3
"""Collision mesh glb -> collision.bin, the flat form the runtime reads.

    python3 tools/glb_to_collision.py in.glb out/collision.bin [--reverse]

The runtime has no glTF parser and does not need one. It needs vertices and triangle
indices, and nothing else in the glb is used - no normals, no materials, no scene graph.
So the format is the two arrays with a header, matching how the splat data already ships
(meta.json plus raw binaries read straight into GPU buffers).

    uint32   version = 1
    uint32   vertCount
    uint32   indexCount
    float32  verts[vertCount * 3]
    uint32   indices[indexCount]

Little-endian, which is every platform this runs on.

WINDING: `--reverse` flips every triangle. Which scenes need it is decided by the drop
test in unity/VDGSConverter/Assets/Editor/CollisionTest.cs, per scene - see the comment in
main(). Do not infer it from the signed volume; that was tried and it is wrong for
textilni.

FRAME: passed through unchanged. The mesh must arrive in the same frame as the converted
splat asset the game loads, and that is per scene - build/splats/playroom is unmirrored
while build/splats/drjohnson is mirrored. Mirroring here would be a second place to get it
wrong; do it to the ply before voxelising instead.
"""
import json
import struct
import sys

import numpy as np

GLB_MAGIC = 0x46546C67


def read_glb(path):
    data = open(path, 'rb').read()
    magic, _version, total = struct.unpack_from('<III', data, 0)
    if magic != GLB_MAGIC:
        raise SystemExit(f'{path}: not a glb')
    offset, chunks = 12, []
    while offset < total:
        length, _kind = struct.unpack_from('<II', data, offset)
        offset += 8
        chunks.append(data[offset:offset + length])
        offset += length
    gltf = json.loads(chunks[0].decode('utf8'))
    blob = chunks[1]

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


def signed_volume(verts, tri):
    """Volume by the divergence theorem. Reported for the record, NOT used as a criterion.

    On a simple closed solid its sign says which way the triangles face. Collision meshes
    out of this pipeline are not always that - textilni has 2,383 non-manifold edges - and
    there the number is large, negative, and meaningless: reversing on that signal produced
    a mesh that held 0 of 8 dropped balls where the original held 6.
    """
    v = np.asarray(verts, np.float64)
    a, b, c = v[tri[:, 0]], v[tri[:, 1]], v[tri[:, 2]]
    return float(np.einsum('ij,ij->i', a, np.cross(b, c)).sum() / 6.0)


def main():
    args = [a for a in sys.argv[1:] if a != '--reverse']
    reverse = '--reverse' in sys.argv[1:]
    src, dst = args[0], args[1]
    verts, indices = read_glb(src)

    # The same two checks the runtime reader makes, so a bad mesh fails here rather than
    # on the game machine. SplatCollision.Read rejects both.
    if len(verts) < 3 or len(indices) < 3:
        raise SystemExit(f'{src}: {len(verts)} vertices and {len(indices)} indices is not a mesh')
    if not np.isfinite(np.asarray(verts)).all():
        raise SystemExit(f'{src}: some vertex coordinates are NaN or infinite')
    if len(indices) % 3:
        raise SystemExit(f'{src}: {len(indices)} indices is not a whole number of triangles')
    hi = int(indices.max())
    if hi >= len(verts):
        raise SystemExit(f'{src}: index {hi} out of range for {len(verts)} vertices')

    # Winding is EXPLICIT, per scene, and decided by the drop test - not by the sign of the
    # volume printed below. That sign was used as an automatic criterion for one afternoon
    # and it is wrong:
    #
    #   playroom    -24.51 m^3   reversing it is CORRECT   (ball reaches the floor)
    #   drjohnson  -107.97 m^3   reversing it is CORRECT
    #   textilni  -2571.04 m^3   reversing it is WRONG     (0 of 8 balls held, vs 6 of 8)
    #
    # All three report a negative volume; two need reversing and one must be left alone.
    # textilni is not a simple closed solid - 2,383 non-manifold edges - and it is also the
    # only one of the three whose triangle count hit the 500K decimation budget, so either
    # the level set or fast_simplification is producing a different convention there. The
    # mechanism is NOT understood; what is measured is which winding holds a dropped ball.
    #
    # PhysX treats a non-convex MeshCollider as SINGLE-SIDED, which is why this matters at
    # all: a body reaching a triangle from behind is not stopped, it passes through and gets
    # held from the far side. On playroom that put a ball 31 mm into the ceiling's underside,
    # 2.2 m above a floor it never touched - and the old single-drop test called it PASS,
    # because the ball did stop.
    #
    # Verify every mesh either way before shipping it:
    #   Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
    #         -executeMethod CollisionTest.Run -vdgsCollision <dir or .ply>
    tri = np.asarray(indices, np.int64).reshape(-1, 3)
    vol = signed_volume(verts, tri)
    if reverse:
        tri = tri[:, ::-1]
        print(f'   winding reversed (--reverse): {vol:+.2f} -> {signed_volume(verts, tri):+.2f} m^3')
    else:
        print(f'   winding kept as generated: {vol:+.2f} m^3   '
              '(sign is NOT the criterion - run CollisionTest)')
    indices = tri.reshape(-1)

    verts = np.ascontiguousarray(verts, '<f4')
    indices = np.ascontiguousarray(indices, '<u4')

    with open(dst, 'wb') as f:
        f.write(struct.pack('<III', 1, len(verts), len(indices)))
        f.write(verts.tobytes())
        f.write(indices.tobytes())

    lo, hi3 = verts.min(0), verts.max(0)
    print(f'   {len(verts):,} verts  {len(indices) // 3:,} tris  '
          f'{(12 + verts.nbytes + indices.nbytes) / 1e6:.1f} MB')
    print(f'   bounds {np.round(lo, 2)} .. {np.round(hi3, 2)}')
    print(f'   wrote {dst}')


if __name__ == '__main__':
    main()
