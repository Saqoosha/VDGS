#!/usr/bin/env python3
"""飛行域から見た**角度の大きさ**で splat を落とす。世界座標の大きさでは切らない。

`--max-sigma`（シーンの広がりの %）も絶対メートルも、どちらも視点を持たない。だから
**遠くの背景を担う大きい splat**と**目の前を覆う霞**を区別できない。同じ 5m でも、
100m 先なら背景、2m 先なら視界全部。

ここで測るのは「コース内側の視点から見て、画面の何画素ぶんを覆うか」:

    投影半径 (px) = 最長軸 * 焦点距離 / 距離

視点は飛行高度に格子状に置き、**距離の 10 パーセンタイル**を使う（＝近いほうから 1 割の
視点で、これだけ覆う）。最近傍 1 点だと、たまたま splat の中に入った視点に支配される。

不透明度は掛けない。**薄くて巨大なものも霞になる**ので、大きさだけで判定して
生きているもの（不透明度 > 0.1）だけを対象にする。

    python3 angcut.py in.ply                      # 測るだけ
    python3 angcut.py in.ply out.ply --max-px 120
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst', nargs='?')
ap.add_argument('--max-px', type=float, default=None, help='投影半径の上限（画素）')
ap.add_argument('--h', type=float, nargs='+', default=[1.0, 2.0, 3.0])
ap.add_argument('--n', type=int, default=64)
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
smax = np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1)
              .astype(np.float64)).max(1)
live = op > 0.1
foc = a.res / (2 * np.tan(np.radians(a.fov) / 2))

lo, hi = np.percentile(P[live][:, [0, 2]], [10, 90], axis=0)
g = int(np.sqrt(a.n))
floor = np.percentile(P[live][:, 1], 1)
cams = np.array([[x, floor + hh, z] for hh in a.h
                 for x in np.linspace(lo[0], hi[0], g)
                 for z in np.linspace(lo[1], hi[1], g)])

# 距離の p10 を、全 splat x 全視点を作らずにブロックで求める
d10 = np.empty(len(P))
for i in range(0, len(P), 200_000):
    blk = P[i:i + 200_000]
    D = np.linalg.norm(blk[:, None, :] - cams[None, :, :], axis=2)
    d10[i:i + 200_000] = np.percentile(D, 10, axis=1)
np.maximum(d10, 0.05, out=d10)
px = smax * foc / d10

# 被覆の寄与（面積 x 不透明度）。どれだけ絵に効くかの目安
cov = px ** 2 * op
tot = cov[live].sum()

print(f'{a.src.split("/")[-1]}  視点 {len(cams)} 箇所  焦点距離 {foc:.1f}px'
      f'（{a.res}px / 画角 {a.fov}度）')
print(f'  投影半径 (px)  p50 {np.percentile(px[live],50):.2f}  p90 {np.percentile(px[live],90):.2f}'
      f'  p99 {np.percentile(px[live],99):.2f}  p99.9 {np.percentile(px[live],99.9):.1f}'
      f'  最大 {px[live].max():.0f}')
print(f'\n  上限 px    落とす個数   生存比    被覆の除去    落とすものの中央値')
for th in (500, 300, 200, 150, 120, 100, 80, 60, 40):
    k = live & (px > th)
    if k.sum() == 0:
        continue
    print(f'  {th:5.0f} px {k.sum():10,} {k.sum()/live.sum()*100:8.3f}% '
          f'{cov[k].sum()/tot*100:10.2f}%    大きさ {np.median(smax[k]):6.3f} m'
          f'  距離 {np.median(d10[k]):6.2f} m  高さ {np.median(P[k,1]-floor):6.2f} m')

if a.max_px is not None and a.dst:
    drop = (px > a.max_px)
    print(f'\n落とす {drop.sum():,} / {n:,} ({drop.sum()/n*100:.3f}%)  '
          f'うち生存 {int((drop & live).sum()):,}  '
          f'被覆の除去 {cov[drop & live].sum()/tot*100:.1f}%')
    out = r[~drop]
    hdr = re.sub(r'element vertex \d+', f'element vertex {len(out)}',
                 txt[:txt.index('end_header')])
    with open(a.dst, 'wb') as gf:
        gf.write(hdr.encode('ascii')); gf.write(b'end_header\n'); gf.write(out.tobytes())
    print(f'wrote {a.dst}  {len(out):,} splats')
