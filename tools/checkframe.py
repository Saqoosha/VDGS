#!/usr/bin/env python3
"""編集して書き出した ply が、元と同じ座標系にいるかを確かめる。

**「切っただけ」を信用しない。** `splat-transform` は読み込みで Z 軸 180 度回す
（ドキュメント通り）。回ったまま焼くと、コリジョンだけ鏡像になって実機で初めて分かる。

判定は**共通部分の一致**で行う。切り出しなので元の部分集合のはずで、位置が同じなら
「元の中から最近傍を引いた距離」がゼロ近傍に集中する。回っていれば散る。
全数比較は重いので無作為抽出して格子ハッシュで引く。

    python3 checkframe.py base.ply edited.ply [edited2.ply ...]
"""
import re, sys
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}


def load(path):
    f = open(path, 'rb'); head = b''
    while b'end_header' not in head:
        head += f.read(1 << 16)
    end = head.index(b'end_header') + len(b'end_header') + 1
    txt = head[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', txt).group(1))
    dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
    r = np.memmap(path, dtype=dt, mode='r', offset=end, shape=(n,))
    P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
    op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
    return P, op, dt.names, n


base, op0, names0, n0 = load(sys.argv[1])
print(f'{sys.argv[1].split("/")[-1]:44s} {n0:10,} splats')
print(f'   bounds {np.round(base.min(0),2)} .. {np.round(base.max(0),2)}')
print(f'   属性 {len(names0)} 個')

# 元を格子ハッシュに入れる（1cm セル）。切り出しなら座標は完全一致するはず
CELL = 0.01
key0 = np.round(base / CELL).astype(np.int64)
h0 = (key0[:, 0] * 73856093) ^ (key0[:, 1] * 19349663) ^ (key0[:, 2] * 83492791)
order = np.argsort(h0); h0s = h0[order]

for p in sys.argv[2:]:
    P, op, names, n = load(p)
    print(f'\n{p.split("/")[-1]:44s} {n:10,} splats  （元の {n/n0*100:.1f}%）')
    print(f'   bounds {np.round(P.min(0),2)} .. {np.round(P.max(0),2)}')
    if len(names) != len(names0):
        print(f'   ** 属性の数が違う: {len(names)} vs {len(names0)} **')
    rng = np.random.default_rng(0)
    s = P[rng.choice(n, min(200000, n), replace=False)]
    k = np.round(s / CELL).astype(np.int64)
    h = (k[:, 0] * 73856093) ^ (k[:, 1] * 19349663) ^ (k[:, 2] * 83492791)
    i = np.searchsorted(h0s, h)
    hit = (i < len(h0s)) & (h0s[np.minimum(i, len(h0s)-1)] == h)
    print(f'   元と 1cm 以内で一致した割合  {hit.mean()*100:.2f}%'
          f'   {"→ 同じ座標系。切っただけ" if hit.mean() > 0.9 else "→ ★ 座標が変わっている"}')
    lo = np.maximum(base.min(0), P.min(0)); hi = np.minimum(base.max(0), P.max(0))
    inb = ((base >= lo) & (base <= hi)).all(1)
    print(f'   元のうち、この箱に入る splat {inb.sum():,} / {n0:,}'
          f'   → 箱の中で残った割合 {n/max(inb.sum(),1)*100:.1f}%')
