#!/usr/bin/env python3
"""splat 雲の「板」としての向きと厚みを測る。地面法線が本当に +Y かを確かめる。

平らな会場なら splat は薄い板になり、**いちばん薄い主成分が地面法線**になる。それが
Y から大きくずれていれば、`align_ply.py` の床推定（Y の 1 パーセンタイル）が的を外し、
ゲーム内で傾く。

外れ値に主成分を握らせないため、中心から遠いものを落としてから測る。
"""
import re, sys
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
p = sys.argv[1]
keep = float(sys.argv[2]) if len(sys.argv) > 2 else 0.95
f = open(p, 'rb'); head = b''
while b'end_header' not in head:
    head += f.read(1 << 16)
end = head.index(b'end_header') + len(b'end_header') + 1
txt = head[:end].decode('ascii', 'replace')
n = int(re.search(r'element vertex (\d+)', txt).group(1))
dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
r = np.array(np.memmap(p, dtype=dt, mode='r', offset=end, shape=(n,)))
P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
P = P[op > 0.1]
c = np.median(P, 0)
d = np.linalg.norm(P - c, axis=1)
P = P[d < np.percentile(d, keep * 100)]
print(f'{p.split("/")[-1]}  使う splat {len(P):,}（中心から p{keep*100:.0f} 以内）')

rng = np.random.default_rng(0)
S = P[rng.choice(len(P), min(300000, len(P)), replace=False)]
S = S - S.mean(0)
_, sv, vt = np.linalg.svd(S, full_matrices=False)
nrm = vt[2] / np.linalg.norm(vt[2])
if nrm[1] < 0:
    nrm = -nrm
print(f'  主成分の広がり  {np.round(sv/np.sqrt(len(S)),1)}')
print(f'  いちばん薄い軸（＝地面法線）  {np.round(nrm,4)}')
print(f'  +Y との角度  {np.degrees(np.arccos(np.clip(abs(nrm[1]),0,1))):.1f} 度')
h = (P - P.mean(0)) @ nrm
print(f'  その軸に沿った厚み  p1..p99 {np.percentile(h,99)-np.percentile(h,1):.1f}'
      f'   p5..p95 {np.percentile(h,95)-np.percentile(h,5):.1f}')
hy = P[:, 1] - np.percentile(P[:, 1], 1)
print(f'  Y に沿った高さ      p50 {np.percentile(hy,50):.1f}  p95 {np.percentile(hy,95):.1f}')
