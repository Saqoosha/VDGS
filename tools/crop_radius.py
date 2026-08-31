#!/usr/bin/env python3
"""中心から一定半径の外に飛んだ splat を落とす。

**床の推定より先にやる。** `align_ply.py` は全体の 1 パーセンタイルを床とするので、
遠方の外れ値が残っていると床が大きくずれる（実測で 100 km の広がりに引きずられ、
床が 15 単位ぶん下にずれた事故がある）。

中心は**生きている splat の中央値**で取る。平均だと外れ値に引かれる。

    python3 crop_radius.py in.ply out.ply --radius 150 --scale 38.024
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                            formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst')
ap.add_argument('--radius', type=float, required=True, help='半径 m')
ap.add_argument('--scale', type=float, required=True, help='m/unit')
a = ap.parse_args()

f = open(a.src, 'rb'); head = b''
while b'end_header' not in head:
    head += f.read(1 << 16)
end = head.index(b'end_header') + len(b'end_header') + 1
txt = head[:end].decode('ascii', 'replace')
n = int(re.search(r'element vertex (\d+)', txt).group(1))
dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
r = np.array(np.memmap(a.src, dtype=dt, mode='r', offset=end, shape=(n,)))

P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
live = op > 0.1
ctr = np.median(P[live], 0)
d = np.linalg.norm(P - ctr, axis=1) * a.scale
keep = d < a.radius
print(f'入力 {n:,}（生存 {live.sum():,}）  中心 {np.round(ctr*a.scale,2)} m')
print(f'半径 {a.radius} m 以内を残す: {keep.sum():,} ({keep.sum()/n*100:.2f}%)  '
      f'落とす {n-keep.sum():,}')

out = r[keep]
hdr = txt[:txt.index('end_header')]
body = re.sub(r'element vertex \d+', f'element vertex {len(out)}', hdr)
with open(a.dst, 'wb') as g:
    g.write(body.encode('ascii')); g.write(b'end_header\n'); g.write(out.tobytes())
Q = np.stack([out['x'], out['y'], out['z']], 1).astype(np.float64) * a.scale
print(f'書き出し後の範囲 (m)  {np.round(Q.min(0),1)} .. {np.round(Q.max(0),1)}')
print(f'wrote {a.dst}')
