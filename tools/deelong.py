#!/usr/bin/env python3
"""細長い splat を**面積を保ったまま**丸める。縮めない、削らない。

弧の正体は「寝た針」で、縮めると弧は消えるが**覆う面積も減って地面が透ける**。
2 大軸を幾何平均に揃えれば、投影面積はそのままで細長さだけ消える:

    (a, b) -> (sqrt(ab), sqrt(ab))      a*b は不変

**被覆モデルもここで直す。** これまで全 splat を「半径＝最長軸の円」と見なしていたが、
0.6m x 0.01m の針の実面積はその 60 分の 1。凸体の平均投影面積は表面積の 1/4
（Cauchy）で、楕円体では **2 大軸の積が支配的**。だから重みは a*b を使う。
針の寄与 70.66% はこの誤りによる過大評価だった。

    python3 deelong.py in.ply                        # 測るだけ
    python3 deelong.py in.ply out.ply --min-elong 3
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst', nargs='?')
ap.add_argument('--min-elong', type=float, default=None, help='丸める細長さの下限 (長/中)')
ap.add_argument('--band', type=float, default=-1, help='地面から何 m を対象に。0 以下で全体')
ap.add_argument('--cell', type=float, default=2.0)
ap.add_argument('--h', type=float, nargs='+', default=[0.3, 0.6, 1.0])
ap.add_argument('--n', type=int, default=36)
ap.add_argument('--fov', type=float, default=120.0)
ap.add_argument('--res', type=int, default=1024)
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
S = np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1).astype(np.float64))
live = op > 0.1
ordS = np.argsort(S, 1)
smin = np.take_along_axis(S, ordS[:, :1], 1)[:, 0]
smid = np.take_along_axis(S, ordS[:, 1:2], 1)[:, 0]
smax = np.take_along_axis(S, ordS[:, 2:], 1)[:, 0]
elong = smax / np.maximum(smid, 1e-9)
area = smax * smid                    # 平均投影面積の代理（2 大軸の積）

# --- 局所地面（視点を置く高さの基準） ---------------------------------------
vote = live & (smax < 0.15) & (op > 0.5)
lo = P[vote][:, [0, 2]].min(0); hi = P[vote][:, [0, 2]].max(0)
nx = int(np.ceil((hi[0]-lo[0])/a.cell))+1; nz = int(np.ceil((hi[1]-lo[1])/a.cell))+1
ix = np.clip(((P[:, 0]-lo[0])/a.cell).astype(int), 0, nx-1)
iz = np.clip(((P[:, 2]-lo[1])/a.cell).astype(int), 0, nz-1)
cid = ix*nz + iz
gy = np.full(nx*nz, np.nan)
vc, vy = cid[vote], P[vote, 1]
o = np.argsort(vc, kind='stable'); vc, vy = vc[o], vy[o]
bnd = np.searchsorted(vc, np.arange(nx*nz+1))
for c in range(nx*nz):
    s, e = bnd[c], bnd[c+1]
    if e-s >= 8:
        gy[c] = np.percentile(vy[s:e], 10)
G = gy.reshape(nx, nz)
for _ in range(40):
    m = np.isnan(G)
    if not m.any():
        break
    acc = np.zeros_like(G); cnt = np.zeros_like(G)
    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        Sft = np.full_like(G, np.nan)
        Sft[max(dx, 0):nx+min(dx, 0), max(dz, 0):nz+min(dz, 0)] = \
            G[max(-dx, 0):nx+min(-dx, 0), max(-dz, 0):nz+min(-dz, 0)]
        ok = ~np.isnan(Sft); acc[ok] += Sft[ok]; cnt[ok] += 1
    fl = m & (cnt > 0); G[fl] = acc[fl]/cnt[fl]
G = np.nan_to_num(G, nan=0.0); gflat = G.reshape(-1)
h = P[:, 1] - gflat[cid]
band = live.copy() if a.band <= 0 else (live & (h > -0.5) & (h < a.band))

# --- 低い視点からの被覆（面積 = 2 大軸の積） --------------------------------
foc = a.res/(2*np.tan(np.radians(a.fov)/2))
g = int(np.sqrt(a.n))
cams = np.array([[xx, gflat[np.clip(int((xx-lo[0])/a.cell), 0, nx-1)*nz
                            + np.clip(int((zz-lo[1])/a.cell), 0, nz-1)] + hh, zz]
                 for hh in a.h
                 for xx in np.linspace(*np.percentile(P[live][:, 0], [15, 85]), g)
                 for zz in np.linspace(*np.percentile(P[live][:, 2], [15, 85]), g)])
inv = np.zeros(n)
for i in range(0, n, 200_000):
    D = np.linalg.norm(P[i:i+200_000, None, :]-cams[None, :, :], axis=2)
    np.maximum(D, 0.05, out=D)
    inv[i:i+200_000] = (1.0/D**2).mean(1)
cov = area * foc**2 * inv * op
tot = cov[live].sum()

print(f'{a.src.split("/")[-1]}  生存 {live.sum():,} / {n:,}  視点 {len(cams)} 箇所')
print(f'  細長さ (長/中)  p50 {np.percentile(elong[live],50):.2f}'
      f'  p90 {np.percentile(elong[live],90):.2f}  p99 {np.percentile(elong[live],99):.2f}'
      f'  最大 {elong[live].max():.0f}')
print(f'  面積 a*b (m^2)  p50 {np.percentile(area[live],50):.5f}'
      f'  p99 {np.percentile(area[live],99):.4f}  最大 {area[live].max():.2f}')
print(f'\n  被覆の上位（面積 a*b で重みづけ。これが正しい重み）')
o2 = np.argsort(-cov*live)
cs = np.cumsum(cov[o2])/tot
for N in (100, 1000, 10000, 100000):
    k = o2[:N]
    print(f'   上位 {N:7,} ({N/live.sum()*100:6.3f}%)  {cs[N-1]*100:6.2f}%  '
          f'長軸 p50 {np.median(smax[k]):7.3f} m  細長さ p50 {np.median(elong[k]):6.2f}'
          f'  面積 p50 {np.median(area[k]):.4f}')
print(f'\n  細長さの下限  丸める個数  生存比   面積の変化   長軸 p50 -> 後')
for e in (10, 6, 4, 3, 2.5, 2):
    k = band & (elong > e)
    if k.sum() == 0:
        continue
    ns = np.sqrt(smax[k]*smid[k])
    print(f'  {e:6.1f} {k.sum():11,} {k.sum()/live.sum()*100:7.2f}% '
          f'{(ns*ns).sum()/area[k].sum()*100:10.2f}% {np.median(smax[k]):9.3f} -> {np.median(ns):.3f}')

if a.min_elong is None or not a.dst:
    raise SystemExit

hit = band & (elong > a.min_elong)
gm = np.sqrt(smax[hit]*smid[hit])
Snew = S.copy()
# put_along_axis は配列全体と同じ長さの索引を要求する。部分に書いてから戻す。
sub = Snew[hit]
np.put_along_axis(sub, ordS[hit, 2:], gm[:, None], 1)
np.put_along_axis(sub, ordS[hit, 1:2], gm[:, None], 1)
Snew[hit] = sub
na = np.sort(Snew, 1)[:, 2] * np.sort(Snew, 1)[:, 1]
print(f'\n丸める {hit.sum():,}（生存の {hit.sum()/live.sum()*100:.2f}%）')
print(f'  検算 面積 a*b  前 {area[hit].sum():.4e} -> 後 {na[hit].sum():.4e}'
      f'  （{na[hit].sum()/area[hit].sum()*100:.4f}%、100% なら完全保存）')
print(f'  細長さ  前 p50 {np.median(elong[hit]):.2f} 最大 {elong[hit].max():.0f}'
      f'  -> 後 最大 {(np.sort(Snew,1)[:,2]/np.maximum(np.sort(Snew,1)[:,1],1e-9))[hit].max():.4f}')
out = r.copy()
for i in range(3):
    out[f'scale_{i}'] = np.log(Snew[:, i]).astype(np.float32)
hdr = txt[:txt.index('end_header')]
with open(a.dst, 'wb') as gf:
    gf.write(hdr.encode('ascii')); gf.write(b'end_header\n'); gf.write(out.tobytes())
print(f'wrote {a.dst}  {len(out):,} splats')
