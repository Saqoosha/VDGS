#!/usr/bin/env python3
"""**局所地面を基準に** Y を切る。地下の層をコリジョン用 ply から落とすため。

`ycrop.py` は全体の Y の 1 パーセンタイルを床にするので、**地形に起伏があると片側で
切りすぎ、片側で切り足りない**。こちらは XZ 格子ごとの地面高さを基準にする。

**地面高さのパーセンタイルは用途で変える。**
- `--pct 10`（`groundfill.py` の既定）は**草や低木の下の床**を探す設計。地下に層があると
  その底を拾ってしまう
- `--pct 50`（ここの既定）は**見えている芝の面**。3DGS の地面は面の上下に splat が
  散るので、中央値がいちばん面に近い

FDF で実測: コリジョン殻の 64% が y<0 に出ていた。地下の層をそのまま包んでいたため。

    python3 groundcrop.py in.ply out.ply --lo -0.5 --hi 14
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst', nargs='?')
ap.add_argument('--lo', type=float, default=-0.5, help='局所地面からの下限 m')
ap.add_argument('--hi', type=float, default=14.0, help='上限 m')
ap.add_argument('--gcell', type=float, default=2.0, help='地面格子 m')
ap.add_argument('--pct', type=float, default=50.0, help='地面とみなすセル内パーセンタイル')
ap.add_argument('--erode', type=int, default=2, help='地面推定にかける最小値フィルタの回数')
ap.add_argument('--vote-size', type=float, default=0.15)
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
op = 1/(1+np.exp(-r['opacity'].astype(np.float64)))
S = np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1).astype(np.float64))
live = op > 0.1
smax = S.max(1)

vote = live & (smax < a.vote_size) & (op > 0.5)
lo, hi = P[vote][:, [0, 2]].min(0), P[vote][:, [0, 2]].max(0)
gx = int(np.ceil((hi[0]-lo[0])/a.gcell))+1; gz = int(np.ceil((hi[1]-lo[1])/a.gcell))+1
gi = np.clip(((P[:, 0]-lo[0])/a.gcell).astype(int), 0, gx-1)
gk = np.clip(((P[:, 2]-lo[1])/a.gcell).astype(int), 0, gz-1)
gid = gi*gz + gk
gy = np.full(gx*gz, np.nan)
vc, vy = gid[vote], P[vote, 1]
o = np.argsort(vc, kind='stable'); vc, vy = vc[o], vy[o]
bnd = np.searchsorted(vc, np.arange(gx*gz+1))
for c in range(gx*gz):
    s, e = bnd[c], bnd[c+1]
    if e-s >= 8:
        gy[c] = np.percentile(vy[s:e], a.pct)
seeded = int(np.isfinite(gy).sum())
G = gy.reshape(gx, gz)
for _ in range(60):
    m = np.isnan(G)
    if not m.any():
        break
    acc = np.zeros_like(G); cnt = np.zeros_like(G)
    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        Sf = np.full_like(G, np.nan)
        Sf[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
            G[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
        ok = ~np.isnan(Sf); acc[ok] += Sf[ok]; cnt[ok] += 1
    fl = m & (cnt > 0); G[fl] = acc[fl]/cnt[fl]
G = np.nan_to_num(G, nan=float(np.nanmedian(gy)) if seeded else 0.0)
if a.erode > 0:
    E = G.copy()
    for _ in range(a.erode):
        nb = E.copy()
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            Sf = np.full_like(E, np.inf)
            Sf[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
                E[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
            nb = np.minimum(nb, Sf)
        E = nb
    for _ in range(2):
        acc = E.copy(); cnt = np.ones_like(E)
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            Sf = np.full_like(E, np.nan)
            Sf[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
                E[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
            ok = ~np.isnan(Sf); acc[ok] += Sf[ok]; cnt[ok] += 1
        E = acc/cnt
    print(f'  最小値フィルタ {a.erode} 回: p50 {np.median(G):.2f} -> {np.median(E):.2f}'
          f'   p99 {np.percentile(G,99):.2f} -> {np.percentile(E,99):.2f} m')
    G = E
gf = G.reshape(-1)
h = P[:, 1] - gf[gid]

print(f'{a.src.split("/")[-1]}  {n:,} splats  地面格子 {gx}x{gz}（実測 {seeded:,}、p{a.pct:.0f}）')
print(f'  局所地面  最小 {G.min():.2f}  p50 {np.median(G):.2f}  最大 {G.max():.2f} m')
print(f'  局所地面からの高さ  p1 {np.percentile(h[live],1):6.2f}  p10 {np.percentile(h[live],10):6.2f}'
      f'  p50 {np.percentile(h[live],50):6.2f}  p90 {np.percentile(h[live],90):6.2f}')
keep = (h >= a.lo) & (h <= a.hi)
print(f'  残す {a.lo} .. {a.hi} m : {keep.sum():,} ({keep.mean()*100:.2f}%)'
      f'   落とす 下 {int((h < a.lo).sum()):,}  上 {int((h > a.hi).sum()):,}')
if not a.dst:
    raise SystemExit
out = r[keep]
hdr = re.sub(r'element vertex \d+', f'element vertex {len(out)}', txt[:txt.index('end_header')])
with open(a.dst, 'wb') as g:
    g.write(hdr.encode('ascii')); g.write(b'end_header\n'); g.write(out.tobytes())
print(f'wrote {a.dst}  {len(out):,} splats')
