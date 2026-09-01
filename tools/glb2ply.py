#!/usr/bin/env python3
"""glb のメッシュを ply に戻す。`clean_mesh.py` と `decimate_mesh.py` が ply しか読まないため。

`gs_field_mesh.py` は glb を直接吐くが、掃除と間引きの道具は OpenVDB 経路（ply）に
合わせて書かれている。**道具側を書き換えると座標系を間違える場所が増える**ので、
形式だけ合わせる。頂点と三角形以外は捨てる（元々それしか入っていない）。

    python3 glb2ply.py in.glb out.ply
"""
import json, struct, sys
import numpy as np

GLB_MAGIC = 0x46546C67


def read_glb(path):
    data = open(path, 'rb').read()
    magic, _ver, total = struct.unpack_from('<III', data, 0)
    if magic != GLB_MAGIC:
        raise SystemExit(f'{path}: not a glb')
    off, chunks = 12, []
    while off < total:
        length, _kind = struct.unpack_from('<II', data, off)
        off += 8
        chunks.append(data[off:off + length])
        off += length
    gltf = json.loads(chunks[0].decode('utf8'))
    blob = chunks[1]

    def accessor(i):
        acc = gltf['accessors'][i]
        view = gltf['bufferViews'][acc['bufferView']]
        start = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
        kind = {5126: '<f4', 5125: '<u4', 5123: '<u2'}[acc['componentType']]
        w = 3 if acc['type'] == 'VEC3' else 1
        flat = np.frombuffer(blob, dtype=kind, count=acc['count'] * w, offset=start)
        return flat.reshape(-1, w) if w > 1 else flat

    prim = gltf['meshes'][0]['primitives'][0]
    return accessor(prim['attributes']['POSITION']), accessor(prim['indices'])


src, dst = sys.argv[1], sys.argv[2]
V, I = read_glb(src)
V = np.asarray(V, np.float32)
T = np.asarray(I, np.uint32).reshape(-1, 3)
print(f'{src.split("/")[-1]}  {len(V):,} verts  {len(T):,} tris')
print(f'  bounds {np.round(V.min(0),2)} .. {np.round(V.max(0),2)}')

with open(dst, 'wb') as f:
    f.write(('ply\nformat binary_little_endian 1.0\n'
             f'element vertex {len(V)}\n'
             'property float x\nproperty float y\nproperty float z\n'
             f'element face {len(T)}\n'
             'property list uchar uint vertex_indices\n'
             'end_header\n').encode('ascii'))
    f.write(V.tobytes())
    face = np.empty((len(T), 13), np.uint8)
    face[:, 0] = 3
    face[:, 1:] = T.view(np.uint8).reshape(-1, 12)
    f.write(face.tobytes())
print(f'wrote {dst}')
