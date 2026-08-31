#!/usr/bin/env python3
"""地面近くの大きい splat を、絶対メートルで測って落とす。

**`align_ply.py --max-sigma` は使えない。** あれは「シーンの広がりの何%」で切るので、
194m のシーンでは 9.7m 未満を全部通す —— 実測で 52 万個中 1 個しか捕まえなかった。
**中に入って飛ぶ用途では 1m の splat が既に巨大。**

前回 JDL で測った実績:
  最長軸 > 1.0m は 5,407 個（生存の 1.45%）で描画面積の 33%
  切った結果、実機の空パッチが 84.68 → 6.14（13.8 分の 1）

入力は実スケールに焼いた後の ply。床は y=0、上が +Y。

    python3 nearground.py in.ply                      # 測るだけ
    python3 nearground.py in.ply out.ply --max-size 1.0
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
C0 = 0.28209479177387814


def load(path):
    f = open(path, 'rb'); head = b''
    while b'end_header' not in head:
        head += f.read(1 << 16)
    end = head.index(b'end_header') + len(b'end_header') + 1
    txt = head[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', txt).group(1))
    dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
    return txt[:txt.index('end_header')], np.array(
        np.memmap(path, dtype=dt, mode='r', offset=end, shape=(n,)))


ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst', nargs='?')
ap.add_argument('--max-size', type=float, default=None, help='最長軸の上限 m')
ap.add_argument('--below', type=float, default=None, help='この高さ未満だけ対象にする m')
ap.add_argument('--band', type=float, default=3.0, help='「地面近く」の高さ m')
a = ap.parse_args()

hdr, r = load(a.src)
P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
live = op > 0.1
S = np.sort(np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1)
                   .astype(np.float64)), 1)
rgb = np.clip(np.stack([r['f_dc_0'], r['f_dc_1'], r['f_dc_2']], 1)
              .astype(np.float64) * C0 + 0.5, 0, 1)
smax = S[:, 2]
# log 空間の中間軸。0 が針、1 が板。3DGS の床と壁は板で、それは正常
t = (np.log(np.maximum(S[:, 1], 1e-9)) - np.log(np.maximum(S[:, 0], 1e-9))) / \
    np.maximum(np.log(np.maximum(S[:, 2], 1e-9)) - np.log(np.maximum(S[:, 0], 1e-9)), 1e-9)
h = P[:, 1]
near = live & (h > -1.0) & (h < a.band)

print(f'{a.src.split("/")[-1]}  生存 {live.sum():,} / {len(r):,}')
print(f'  最長軸 (m)  p50 {np.percentile(smax[live],50):.3f}  p90 {np.percentile(smax[live],90):.3f}'
      f'  p99 {np.percentile(smax[live],99):.3f}  最大 {smax[live].max():.2f}')
print(f'  地面近く（床+{a.band}m 以内）{near.sum():,}')
# 面積寄与の目安: 大きさの二乗 x 不透明度（視点に依らない）
w = smax ** 2 * op
tot = w[live].sum()
print(f'\n  しきい値   個数    生存比    高さ中央値  不透明度  RGB              形 t   面積寄与')
for th in (0.3, 0.5, 0.8, 1.0, 1.5, 2.0, 3.0):
    k = live & (smax > th)
    if k.sum() == 0:
        continue
    print(f'  {th:5.1f} m {k.sum():8,} {k.sum()/live.sum()*100:7.3f}% '
          f'{np.median(h[k]):9.2f} m {np.median(op[k]):8.2f}  '
          f'{np.median(rgb[k,0]):.2f}/{np.median(rgb[k,1]):.2f}/{np.median(rgb[k,2]):.2f} '
          f'{np.median(t[k]):6.2f} {w[k].sum()/tot*100:8.2f}%')

print(f'\n  高さ帯ごとの最長軸')
for lo, hi, lbl in ((-100, 0, '床より下'), (0, 2, '0-2m'), (2, 5, '2-5m'),
                    (5, 15, '5-15m'), (15, 500, '15m 以上')):
    k = live & (h >= lo) & (h < hi)
    if k.sum() == 0:
        continue
    print(f'   {lbl:10s} {k.sum():9,}  p50 {np.median(smax[k]):.3f}  '
          f'p99 {np.percentile(smax[k],99):6.3f}  最大 {smax[k].max():7.2f}  '
          f'>1m {int((k & (smax>1.0)).sum()):7,}')

if a.max_size is None or not a.dst:
    return_ = True
else:
    drop = smax > a.max_size
    if a.below is not None:
        drop &= h < a.below
    print(f'\n落とす {drop.sum():,} ({drop.sum()/len(r)*100:.3f}%)  '
          f'うち生存 {int((drop & live).sum()):,}  '
          f'面積寄与 {w[drop & live].sum()/tot*100:.1f}%')
    out = r[~drop]
    body = re.sub(r'element vertex \d+', f'element vertex {len(out)}', hdr)
    with open(a.dst, 'wb') as f:
        f.write(body.encode('ascii')); f.write(b'end_header\n'); f.write(out.tobytes())
    print(f'wrote {a.dst}  {len(out):,} splats')
