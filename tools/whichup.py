#!/usr/bin/env python3
"""地面法線のどちら側が空かを測る。**`updir.py` は Y 方向しか見ないので、倒れた
シーンでは判定が無意味になる**（FDF-airvis は Z が上で、updir は WEAK を返した）。

やり方: PCA のいちばん薄い軸を地面法線とし、その軸に射影した密度の山（＝地面）を見つけ、
**山の両側の質量を比べる**。屋外キャプチャなら、空側には木・ゲート・建物が伸びていて、
地面側にはほとんど何も無い。

    python3 whichup.py in.ply
"""
import re, sys
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
p = sys.argv[1]
f = open(p, 'rb'); head = b''
while b'end_header' not in head:
    head += f.read(1 << 16)
end = head.index(b'end_header') + len(b'end_header') + 1
txt = head[:end].decode('ascii', 'replace')
n = int(re.search(r'element vertex (\d+)', txt).group(1))
dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
r = np.memmap(p, dtype=dt, mode='r', offset=end, shape=(n,))
P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
P = P[op > 0.1]
c = np.median(P, 0)
d = np.linalg.norm(P - c, axis=1)
P = P[d < np.percentile(d, 95)]

rng = np.random.default_rng(0)
S = P[rng.choice(len(P), min(400000, len(P)), replace=False)]
S = S - S.mean(0)
_, sv, vt = np.linalg.svd(S, full_matrices=False)
nrm = vt[2] / np.linalg.norm(vt[2])
print(f'{p.split("/")[-1]}  生存 {len(P):,}')
print(f'  地面法線  {np.round(nrm, 4)}   広がり {np.round(sv/np.sqrt(len(S)), 2)}')

h = (P - P.mean(0)) @ nrm
# 密度の山＝地面。ヒストグラムの最頻ビン
cnt, edge = np.histogram(h, bins=400)
peak = (edge[cnt.argmax()] + edge[cnt.argmax()+1]) / 2
print(f'  法線に沿った密度の山（＝地面）  {peak:.3f}')
for side, sel in (('+側', h > peak), ('-側', h < peak)):
    v = h[sel] - peak
    print(f'  {side}  質量 {sel.mean()*100:5.1f}%   厚み p95 {np.percentile(np.abs(v),95):6.2f}'
          f'   p99.9 {np.percentile(np.abs(v),99.9):6.2f}   最大 {np.abs(v).max():7.2f}')
plus = np.percentile(np.abs(h[h > peak] - peak), 99.9)
minus = np.percentile(np.abs(h[h < peak] - peak), 99.9)
up = nrm if plus > minus else -nrm
print(f'\n  ** 上（空）は {np.round(up, 4)} 側 **  —— そちらのほうが {max(plus,minus)/max(min(plus,minus),1e-9):.1f} 倍高くまで伸びている')
# +Y に持っていく回転（X 軸まわり、次に Z 軸まわり）を出す
import math
print(f'  +Y との角度 {math.degrees(math.acos(np.clip(up[1], -1, 1))):.1f} 度')
print(f'  X 軸まわりに {math.degrees(math.atan2(up[2], up[1])):.1f} 度 回すと Y 上になる'
      f'（残り Z 軸まわり {math.degrees(math.atan2(-up[0], math.hypot(up[1], up[2]))):.1f} 度）')
