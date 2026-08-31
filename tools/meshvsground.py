#!/usr/bin/env python3
"""コリジョン殻が**局所地面に沿っているか**を測る。

**`y=0` を基準にしてはいけない。** 地形に起伏があると（FDF は局所地面が -2.21〜9.36 m）、
面に沿った殻でも頂点の半分が y<0 に来る。「64% が y<0」は欠陥の証拠にならない —— 一度
そう誤読した。

正しくは、splat から作った局所地面の格子に対して、メッシュ頂点の高さを見る。
沿っていれば 0 のまわりに集まり、地下の塊を包んでいれば下に尾を引く。

    python3 meshvsground.py source.ply mesh.glb [mesh2.glb ...]
"""
import json, re, struct, sys
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
GLB_MAGIC = 0x46546C67


def read_glb(path):
    data = open(path, 'rb').read()
    magic, _v, total = struct.unpack_from('<III', data, 0)
    if magic != GLB_MAGIC:
        raise SystemExit(f'{path}: not a glb')
    off, chunks = 12, []
    while off < total:
        length, _k = struct.unpack_from('<II', data, off)
        off += 8
        chunks.append(data[off:off+length]); off += length
    gltf = json.loads(chunks[0].decode('utf8')); blob = chunks[1]

    def acc(i):
        a = gltf['accessors'][i]
        v = gltf['bufferViews'][a['bufferView']]
        st = v.get('byteOffset', 0) + a.get('byteOffset', 0)
        kind = {5126: '<f4', 5125: '<u4', 5123: '<u2'}[a['componentType']]
        w = 3 if a['type'] == 'VEC3' else 1
        fl = np.frombuffer(blob, dtype=kind, count=a['count']*w, offset=st)
        return fl.reshape(-1, w) if w > 1 else fl
    prim = gltf['meshes'][0]['primitives'][0]
    return np.asarray(acc(prim['attributes']['POSITION']), float)


src = sys.argv[1]
f = open(src, 'rb'); head = b''
while b'end_header' not in head:
    head += f.read(1 << 16)
end = head.index(b'end_header') + len(b'end_header') + 1
txt = head[:end].decode('ascii', 'replace')
n = int(re.search(r'element vertex (\d+)', txt).group(1))
dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
r = np.memmap(src, dtype=dt, mode='r', offset=end, shape=(n,))
P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
op = 1/(1+np.exp(-r['opacity'].astype(np.float64)))
S = np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1).astype(np.float64))
live = op > 0.1
GC = 2.0
vote = live & (S.max(1) < 0.15) & (op > 0.5)
lo, hi = P[vote][:, [0, 2]].min(0), P[vote][:, [0, 2]].max(0)
gx = int(np.ceil((hi[0]-lo[0])/GC))+1
gz = int(np.ceil((hi[1]-lo[1])/GC))+1
gid = (np.clip(((P[:, 0]-lo[0])/GC).astype(int), 0, gx-1)*gz
       + np.clip(((P[:, 2]-lo[1])/GC).astype(int), 0, gz-1))
gy = np.full(gx*gz, np.nan)
vc, vy = gid[vote], P[vote, 1]
o = np.argsort(vc, kind='stable'); vc, vy = vc[o], vy[o]
bnd = np.searchsorted(vc, np.arange(gx*gz+1))
for c in range(gx*gz):
    s, e = bnd[c], bnd[c+1]
    if e-s >= 8:
        gy[c] = np.median(vy[s:e])
G = gy.reshape(gx, gz)
for _ in range(60):
    m = np.isnan(G)
    if not m.any():
        break
    acc2 = np.zeros_like(G); cnt = np.zeros_like(G)
    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        Sf = np.full_like(G, np.nan)
        Sf[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
            G[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
        ok = ~np.isnan(Sf); acc2[ok] += Sf[ok]; cnt[ok] += 1
    fl = m & (cnt > 0); G[fl] = acc2[fl]/cnt[fl]
G = np.nan_to_num(G, nan=float(np.nanmedian(gy)))
gf = G.reshape(-1)
hs = P[live][:, 1] - gf[gid[live]]
print(f'{src.split("/")[-1]}  地面格子 {gx}x{gz}  局所地面 p50 {np.median(G):.2f} m')
print(f'  splat の局所地面からの高さ  p5 {np.percentile(hs,5):6.2f}  '
      f'p50 {np.percentile(hs,50):6.2f}  p95 {np.percentile(hs,95):6.2f}')
print(f'\n  {"メッシュ":24s} {"p5":>7s} {"p25":>7s} {"p50":>7s} {"p75":>7s} {"p95":>7s} {"-1m 未満":>9s}')
for m in sys.argv[2:]:
    V = read_glb(m)
    gi = np.clip(((V[:, 0]-lo[0])/GC).astype(int), 0, gx-1)
    gk = np.clip(((V[:, 2]-lo[1])/GC).astype(int), 0, gz-1)
    h = V[:, 1] - gf[gi*gz+gk]
    q = [np.percentile(h, x) for x in (5, 25, 50, 75, 95)]
    print(f'  {m.split("/")[-1][:24]:24s} {q[0]:7.2f} {q[1]:7.2f} {q[2]:7.2f} {q[3]:7.2f}'
          f' {q[4]:7.2f} {(h < -1).mean()*100:8.1f}%')
