#!/usr/bin/env python3
"""飛行視点から見た「画面をどれだけ覆うか」で splat を順位づける。

**大きさの二乗で近似してはいけない。** 遠い巨大 splat と近い中くらいの splat が
同じ重みになってしまう。霞は**近くの大きいもの**が作る。距離で割る必要がある。

視点から見た立体角の寄与:  (r * f / d)^2 * alpha
  r = 最長軸（m）、d = 視点からの距離、f = 焦点距離（画素）

ドローンの飛行高度を数点サンプルして、全方位ぶんを合算する。
画角の内外は方位を振って均すので判定しない。
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser()
ap.add_argument('src')
ap.add_argument('--h', type=float, nargs='+', default=[1.0, 2.0, 3.0], help='飛行高度 m')
ap.add_argument('--n', type=int, default=64, help='視点の数（XZ にグリッド配置）')
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
smax = S.max(1)
live = op > 0.1

foc = a.res / (2 * np.tan(np.radians(a.fov) / 2))
# 視点は本体が居るところに置く。外れ値に引かれないよう中央 80% の箱から取る
lo, hi = np.percentile(P[live][:, [0, 2]], [10, 90], axis=0)
g = int(np.sqrt(a.n))
xs = np.linspace(lo[0], hi[0], g); zs = np.linspace(lo[1], hi[1], g)
floor = np.percentile(P[live][:, 1], 1)

cov = np.zeros(len(P))
cams = 0
for hh in a.h:
    for x in xs:
        for z in zs:
            c = np.array([x, floor + hh, z])
            d = np.linalg.norm(P - c, axis=1)
            np.maximum(d, 0.05, out=d)
            cov += (smax * foc / d) ** 2 * op
            cams += 1
cov /= cams
tot = cov[live].sum()

print(f'{a.src.split("/")[-1]}  視点 {cams} 箇所（高度 {a.h} m、画角 {a.fov}度）')
print(f'\n  しきい値    個数    生存比   画面被覆の寄与   累積で残る')
for th in (0.2, 0.3, 0.4, 0.5, 0.8, 1.0, 1.5, 2.0):
    k = live & (smax > th)
    print(f'  {th:5.2f} m {k.sum():9,} {k.sum()/live.sum()*100:7.3f}% '
          f'{cov[k].sum()/tot*100:12.2f}% {100-cov[k].sum()/tot*100:12.2f}%')

print(f'\n  被覆の寄与が大きい順に上位 N 個で、全体の何 % を占めるか')
o = np.argsort(-cov * live)
cs = np.cumsum(cov[o]) / tot
for N in (100, 1000, 5000, 20000, 100000):
    print(f'   上位 {N:7,} 個 ({N/live.sum()*100:6.3f}%)  {cs[N-1]*100:6.2f}%  '
          f'最長軸 p50 {np.median(smax[o[:N]]):.3f} m  高さ p50 {np.median(P[o[:N],1]-floor):6.2f} m')
