"""Binary ply mesh -> glb, frame unchanged.

    python3 tools/mesh_to_glb.py in.ply out.glb

vdb_tool writes ply and builds the mesh in the SOURCE ply's own coordinates. Nothing is
transformed here, and that is the point: an earlier version negated Y, and since the
preview's splat was mirrored to match, the two agreed with each other while the room
rendered upside down. Two wrongs that cancel are invisible in every check that compares
the collision to the capture - they only show up when a person looks at the picture and
recognises the room.

The frame the GAME needs is decided per scene, in reprocess.sh - playroom is deliberately
not mirrored, drjohnson is. See glb_to_collision.py's FRAME note. Do not "fix" a capture
here to match another one.
"""
import json, re, struct, sys
import numpy as np

SIZES = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'char': 'i1',
         'int': '<i4', 'uint': '<u4', 'short': '<i2', 'ushort': '<u2'}


def read(path):
    with open(path, 'rb') as f:
        head = b''
        while b'end_header' not in head:
            c = f.read(65536)
            if not c:
                raise SystemExit('no end_header')
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
        rest = f.read()

    xyz = np.stack([verts['x'], verts['y'], verts['z']], 1).astype(np.float32)

    # Faces are a variable-length list, and vdb_tool emits a mix of triangles and quads
    # because adaptive meshing merges coplanar cells. Walk them and fan-triangulate.
    tris, off = [], 0
    csz = np.dtype(cnt_t).itemsize
    isz = np.dtype(idx_t).itemsize
    for _ in range(nface):
        n = int(np.frombuffer(rest, dtype=cnt_t, count=1, offset=off)[0]); off += csz
        idx = np.frombuffer(rest, dtype=idx_t, count=n, offset=off); off += n * isz
        for k in range(1, n - 1):
            tris.append((idx[0], idx[k], idx[k + 1]))
    return xyz, np.asarray(tris, dtype=np.uint32)


def write(path, verts, tris):
    verts = np.ascontiguousarray(verts, dtype='<f4')
    tris = np.ascontiguousarray(tris, dtype='<u4').reshape(-1)
    blob = verts.tobytes() + tris.tobytes()
    blob += b'\0' * (-len(blob) % 4)
    gltf = {
        'asset': {'version': '2.0', 'generator': 'vdgs ply2glb'},
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
        f.write(struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(head) + 8 + len(blob)))
        f.write(struct.pack('<II', len(head), 0x4E4F534A)); f.write(head)
        f.write(struct.pack('<II', len(blob), 0x004E4942)); f.write(blob)


v, t = read(sys.argv[1])
write(sys.argv[2], v, t)
print(f'{len(t):,} tris  {len(v):,} verts  -> {sys.argv[2]}')
