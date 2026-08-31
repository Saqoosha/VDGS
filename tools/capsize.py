#!/usr/bin/env python3
"""地面近くの splat の**長軸だけ**を上限で頭打ちにする。消さない、縮める。

**σ_y（縦の突き出し）で追ったのは外れだった。** 潰しても絵が変わらず、測り直したら
低い視点の被覆の **70.66% が「寝た針」** —— 地面に寝た細長い線、122 万個。σ_y は
0.017m しかないので、縦を基準にした判定を全部すり抜けていた。

長さの分布には尾がある。p50 は 0.075m（正常な草の粒度）だが p99 は 0.689m で、
**被覆の上位 2 万個の最長軸中央値は 0.46m**。描いているのは尾だけ。

だから**尾だけを切る** —— ローカルの最大スケールを上限で clamp する。向きは触らない
（針の長軸＝ローカル最大スケールなので、これで筋の長さが直接縮む）。**消さないので
穴は増えない。** 被覆は最長軸の二乗に比例するので、cap の効果は再計算せずに出せる。

地面は y=0 ではなく**局所地面**（XZ 格子の各セルで、小さく濃い splat の Y の 10 パーセンタイル）。
実測でシーンの起伏は 8.31 m あり、平面 1 枚では判定を外す。

    python3 capsize.py in.ply                      # 表だけ
    python3 capsize.py in.ply out.ply --cap 0.25
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst', nargs='?')
ap.add_argument('--cap', type=float, default=None, help='長軸の上限 m')
ap.add_argument('--band', type=float, default=3.0)
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
smax = S.max(1)
imax = np.argmax(S, 1)

# --- 局所地面 ---------------------------------------------------------------
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
G = np.nan_to_num(G, nan=0.0)
gflat = G.reshape(-1)
h = P[:, 1] - gflat[cid]
band = live & (h > -0.5) & (h < a.band)

# --- 低い視点からの被覆 -----------------------------------------------------
foc = a.res/(2*np.tan(np.radians(a.fov)/2))
g = int(np.sqrt(a.n))
xs = np.linspace(*np.percentile(P[live][:, 0], [15, 85]), g)
zs = np.linspace(*np.percentile(P[live][:, 2], [15, 85]), g)
cams = np.array([[xx,
                  gflat[np.clip(int((xx-lo[0])/a.cell), 0, nx-1)*nz
                        + np.clip(int((zz-lo[1])/a.cell), 0, nz-1)] + hh, zz]
                 for hh in a.h for xx in xs for zz in zs])
inv = np.zeros(n)          # mean(1/d^2) —— 被覆は smax^2 に比例するので分けておく
for i in range(0, n, 200_000):
    D = np.linalg.norm(P[i:i+200_000, None, :]-cams[None, :, :], axis=2)
    np.maximum(D, 0.05, out=D)
    inv[i:i+200_000] = (1.0/D**2).mean(1)
base = smax**2 * foc**2 * inv * op
tot = base[live].sum()

print(f'{a.src.split("/")[-1]}  生存 {live.sum():,}  視点 {len(cams)} 箇所（高さ {a.h} m）')
print(f'  地面帯 {band.sum():,}  最長軸 p50 {np.median(smax[band]):.3f}'
      f'  p90 {np.percentile(smax[band],90):.3f}  p99 {np.percentile(smax[band],99):.3f}')
print(f'\n  長軸の上限   縮める個数   帯内比   縮小率中央値   被覆の残り')
for c in (0.60, 0.50, 0.40, 0.30, 0.25, 0.20, 0.15, 0.10):
    k = band & (smax > c)
    if k.sum() == 0:
        continue
    ns = np.where(band, np.minimum(smax, c), smax)
    new = (base * (ns/np.maximum(smax, 1e-9))**2)[live].sum()
    print(f'  {c:6.2f} m {k.sum():11,} {k.sum()/band.sum()*100:7.2f}% '
          f'{np.median(c/smax[k]):12.2f} {new/tot*100:12.2f}%')

if a.cap is None or not a.dst:
    raise SystemExit

hit = band & (smax > a.cap)
print(f'\n縮める {hit.sum():,}（帯内の {hit.sum()/band.sum()*100:.2f}%、全体の {hit.sum()/n*100:.2f}%）')
ns = np.where(hit, a.cap, smax)
print(f'  被覆  {tot:.3e} -> {(base*(ns/np.maximum(smax,1e-9))**2)[live].sum():.3e}'
      f'  （{(base*(ns/np.maximum(smax,1e-9))**2)[live].sum()/tot*100:.1f}%）')
Snew = S.copy()
# **最長軸だけ縮めるのは間違い。** 板やお椀では 2 番目の軸も上限を超えていて、
# 縮めたあとにそれが最長になる（検算で 0.25 のはずが 0.966 のまま残って露見した）。
Snew[hit] = np.minimum(S[hit], a.cap)
out = r.copy()
for i in range(3):
    out[f'scale_{i}'] = np.log(Snew[:, i]).astype(np.float32)
chk = Snew.max(1)
print(f'  検算 最長軸  帯内 前 最大 {smax[band].max():.3f} -> 後 最大 {chk[band].max():.3f}'
      f'   帯外は不変 {np.allclose(chk[~band], smax[~band])}')
hdr = txt[:txt.index('end_header')]
with open(a.dst, 'wb') as gf:
    gf.write(hdr.encode('ascii')); gf.write(b'end_header\n'); gf.write(out.tobytes())
print(f'wrote {a.dst}  {len(out):,} splats')
