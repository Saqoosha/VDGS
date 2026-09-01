#!/usr/bin/env python3
"""コリジョンを焼く前に、Y の範囲外を落とす。

**地下の残骸はグリッドの半分を食う。** このシーンは Y が -15.9 まで伸びていて、
密度場のグリッドは丸ごと確保されるので、切らないと res 0.12 で 1.86 GB、
切れば 1.00 GB になる。**そして地面の下に偽の殻を作らせないためでもある。**

床は Y の 1 パーセンタイル（`align_ply` と同じ定義）。

    python3 ycrop.py in.ply out.ply --lo -1 --hi 12
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst')
ap.add_argument('--lo', type=float, default=-1.0, help='床からの下限 m')
ap.add_argument('--hi', type=float, default=12.0, help='床からの上限 m')
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
floor = np.percentile(y[live], 1)
keep = (y >= floor + a.lo) & (y <= floor + a.hi)
print(f'{a.src.split("/")[-1]}  {n:,} splats  床 {floor:.3f}')
print(f'  Y 範囲  {y.min():.2f} .. {y.max():.2f}  ->  残す {floor+a.lo:.2f} .. {floor+a.hi:.2f}')
print(f'  残る {keep.sum():,} ({keep.sum()/n*100:.2f}%)  落とす {n-keep.sum():,}'
      f'（うち生存 {int((~keep & live).sum()):,}）')

out = r[keep]
hdr = re.sub(r'element vertex \d+', f'element vertex {len(out)}', txt[:txt.index('end_header')])
with open(a.dst, 'wb') as g:
    g.write(hdr.encode('ascii')); g.write(b'end_header\n'); g.write(out.tobytes())
P = np.stack([out['x'], out['y'], out['z']], 1).astype(np.float64)
print(f'  書き出し後の範囲  {np.round(P.min(0),2)} .. {np.round(P.max(0),2)}')
print(f'wrote {a.dst}  {len(out):,} splats')
