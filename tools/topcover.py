#!/usr/bin/env python3
"""真上から見た地面の被覆を XZ 格子で測り、複数の ply を比べて地図に出す。

**「中央に splat が少ない」が元からなのか、こちらが削ったせいなのかで対処が正反対になる。**
削ったせいなら復元（潰して残す）、元からなら実在 splat の複製で埋める。**合成 splat は使わない**
—— FDF で一度やって霞とチラつきが戻っている。

各セルの被覆は、地面帯にある splat の**水平投影面積の和**（2 大軸の積を上限にセル面積で
正規化）。個数ではなく面積で数える —— 大きい splat 1 個は小さい 100 個ぶんを覆う。

    python3 topcover.py a.ply b.ply --out map.png
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('plys', nargs='+')
ap.add_argument('--cell', type=float, default=1.0, help='格子 m')
ap.add_argument('--band', type=float, default=3.0, help='床から何 m を地面とするか')
ap.add_argument('--out', default=None, help='被覆マップの png')
a = ap.parse_args()

maps, names, ext = [], [], None
for p in a.plys:
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
    S = np.sort(np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1)
                       .astype(np.float64)), 1)
    live = op > 0.1
    floor = np.percentile(P[live, 1], 1)
    band = live & (P[:, 1] - floor > -1.0) & (P[:, 1] - floor < a.band)
    area = S[:, 2] * S[:, 1] * np.pi          # 楕円の面積、上限側の 2 軸
    if ext is None:
        ext = (np.percentile(P[live, 0], [0.5, 99.5]), np.percentile(P[live, 2], [0.5, 99.5]))
    (x0, x1), (z0, z1) = ext
    nx = int(np.ceil((x1 - x0) / a.cell)); nz = int(np.ceil((z1 - z0) / a.cell))
    ix = ((P[:, 0] - x0) / a.cell).astype(int)
    iz = ((P[:, 2] - z0) / a.cell).astype(int)
    ok = band & (ix >= 0) & (ix < nx) & (iz >= 0) & (iz < nz)
    M = np.bincount((ix[ok] * nz + iz[ok]), weights=area[ok] * op[ok],
                    minlength=nx * nz).reshape(nx, nz) / (a.cell ** 2)
    maps.append(M); names.append(p.split('/')[-1])
    q = [np.percentile(M, x) for x in (1, 10, 50)]
    print(f'{names[-1]:42s} 帯内 {band.sum():9,}  被覆 p1 {q[0]:6.2f} p10 {q[1]:6.2f} '
          f'p50 {q[2]:6.2f}   1.0未満のセル {(M<1).mean()*100:5.2f}%  '
          f'0.3未満 {(M<0.3).mean()*100:5.2f}%')

if len(maps) >= 2:
    A, B = maps[0], maps[-1]
    lost = (A >= 1.0) & (B < 1.0)
    print(f'\n{names[0]} では覆えていて {names[-1]} で覆えなくなったセル: '
          f'{lost.sum():,} / {A.size:,} ({lost.mean()*100:.2f}%)')
    bare = (A < 1.0)
    print(f'{names[0]} の時点ですでに覆えていないセル: {bare.sum():,} ({bare.mean()*100:.2f}%)')

if a.out:
    from PIL import Image
    W = [np.clip(np.log10(np.maximum(M, 1e-3)) / 2 + 0.5, 0, 1) for M in maps]
    im = np.concatenate([np.rot90(w) for w in W], 1)
    img = Image.fromarray((im * 255).astype(np.uint8))
    k = max(1, int(np.ceil(1600 / img.width)))      # 格子が読める大きさまで最近傍で拡大
    img = img.resize((img.width * k, img.height * k), Image.NEAREST)
    img.save(a.out)
    print(f'wrote {a.out}  {im.shape[1]}x{im.shape[0]}  （左から {", ".join(names)}）')
