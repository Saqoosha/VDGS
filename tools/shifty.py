#!/usr/bin/env python3
"""Y を平行移動して床を 0 に合わせる。

**`align_ply` の床は Y の 1 パーセンタイル**で、地下に伸びた外れ値の尾に引きずられる。
FDF では p0.5 が -3.84m にあり、床が本当の地面より約 3.8m 低く置かれた（splat 高さの
密度の山は 3.75m）。パーセンタイルで決める閾値の弱点で、`--max-sigma` と同じ形。

基準は**splat 高さの密度の山**にする。屋外キャプチャでは地表がいちばん密なので、
外れ値の量に依らない。

    python3 shifty.py in.ply out.ply            # 密度の山を 0 へ
    python3 shifty.py in.ply out.ply --dy -3.75 # 明示
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst')
ap.add_argument('--dy', type=float, default=None, help='明示の移動量 m')
ap.add_argument('--lo', type=float, default=-20.0, help='山を探す範囲の下限 m')
ap.add_argument('--hi', type=float, default=40.0, help='上限 m')
a = ap.parse_args()

f = open(a.src, 'rb'); head = b''
while b'end_header' not in head:
    head += f.read(1 << 16)
end = head.index(b'end_header') + len(b'end_header') + 1
txt = head[:end].decode('ascii', 'replace')
n = int(re.search(r'element vertex (\d+)', txt).group(1))
dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
r = np.array(np.memmap(a.src, dtype=dt, mode='r', offset=end, shape=(n,)))

y = r['y'].astype(np.float64)
op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
live = op > 0.1
w = y[live]
band = w[(w > a.lo) & (w < a.hi)]
cnt, edge = np.histogram(band, bins=240)
peak = (edge[cnt.argmax()] + edge[cnt.argmax()+1]) / 2
dy = a.dy if a.dy is not None else -peak
print(f'{a.src.split("/")[-1]}  生存 {live.sum():,}')
print(f'  密度の山（{a.lo}..{a.hi} m）  {peak:.3f} m   移動量 {dy:+.3f} m')
print(f'  前  p1 {np.percentile(w,1):7.2f}  p10 {np.percentile(w,10):7.2f}'
      f'  p50 {np.percentile(w,50):7.2f}  p90 {np.percentile(w,90):7.2f}')
r['y'] = (y + dy).astype(np.float32)
w2 = r['y'].astype(np.float64)[live]
print(f'  後  p1 {np.percentile(w2,1):7.2f}  p10 {np.percentile(w2,10):7.2f}'
      f'  p50 {np.percentile(w2,50):7.2f}  p90 {np.percentile(w2,90):7.2f}')
hdr = txt[:txt.index('end_header')]
with open(a.dst, 'wb') as g:
    g.write(hdr.encode('ascii')); g.write(b'end_header\n'); g.write(r.tobytes())
print(f'wrote {a.dst}')
