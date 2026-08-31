#!/usr/bin/env python3
"""SFM のカメラから「上」を決める。**質量分布より確か。**

ドローン写真のカメラは地面を見下ろしている。COLMAP のカメラ座標系は +Z が前方なので、
**ワールドでの前方 = R^T [0,0,1] は地面を向く**。その平均の逆が上。スケールも位置も要らない。

同時に、SFM と ply が同じ向きを共有しているかを確かめる —— カメラ中心が張る平面の法線と、
ply の PCA 最薄軸を比べる。一致すれば（符号を除いて）向きは共有。

    python3 camup.py images.txt [scene.ply]
"""
import sys
import numpy as np


def qvec2R(q):
    w, x, y, z = q
    return np.array([
        [1-2*(y*y+z*z), 2*(x*y-w*z), 2*(x*z+w*y)],
        [2*(x*y+w*z), 1-2*(x*x+z*z), 2*(y*z-w*x)],
        [2*(x*z-w*y), 2*(y*z+w*x), 1-2*(x*x+y*y)]])


names, C, F = [], [], []
for line in open(sys.argv[1]):
    if line.startswith('#') or not line.strip():
        continue
    p = line.split()
    if len(p) < 10:                      # 2 行目（特徴点）は飛ばす
        continue
    q = np.array([float(v) for v in p[1:5]])
    t = np.array([float(v) for v in p[5:8]])
    R = qvec2R(q)
    C.append(-R.T @ t)                   # カメラ中心
    F.append(R.T @ np.array([0, 0, 1.]))  # ワールドでの前方（＝地面を向く）
    names.append(p[9])
C = np.array(C); F = np.array(F)
print(f'カメラ {len(C):,} 台  最初 {names[0]}  最後 {names[-1]}')
print(f'  中心の範囲  {np.round(C.min(0),2)} .. {np.round(C.max(0),2)}')

fwd = F.mean(0); fwd /= np.linalg.norm(fwd)
up = -fwd
print(f'  平均の前方（地面向き）  {np.round(fwd,4)}')
print(f'  ** 上 = {np.round(up,4)} **')
spread = np.degrees(np.arccos(np.clip(F @ fwd, -1, 1)))
print(f'  前方のばらつき  中央値 {np.median(spread):.1f} 度  p90 {np.percentile(spread,90):.1f} 度')

Cc = C - C.mean(0)
_, sv, vt = np.linalg.svd(Cc, full_matrices=False)
nrm = vt[2] / np.linalg.norm(vt[2])
print(f'  カメラ中心が張る平面の法線  {np.round(nrm,4)}  '
      f'（広がり {np.round(sv/np.sqrt(len(C)),2)}）')
print(f'  その法線と「上」の角度  {np.degrees(np.arccos(abs(np.clip(nrm@up,-1,1)))):.1f} 度')

if len(sys.argv) > 2:
    import re
    SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
    p = sys.argv[2]
    f = open(p, 'rb'); head = b''
    while b'end_header' not in head:
        head += f.read(1 << 16)
    end = head.index(b'end_header') + len(b'end_header') + 1
    txt = head[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', txt).group(1))
    dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
    r = np.memmap(p, dtype=dt, mode='r', offset=end, shape=(n,))
    P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
    op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
    P = P[op > 0.1]
    d = np.linalg.norm(P - np.median(P, 0), axis=1)
    P = P[d < np.percentile(d, 95)]
    rng = np.random.default_rng(0)
    S = P[rng.choice(len(P), min(300000, len(P)), replace=False)]
    S = S - S.mean(0)
    _, sv2, vt2 = np.linalg.svd(S, full_matrices=False)
    pn = vt2[2] / np.linalg.norm(vt2[2])
    ang = np.degrees(np.arccos(abs(np.clip(pn @ up, -1, 1))))
    print(f'\n  ply の最薄軸  {np.round(pn,4)}')
    print(f'  カメラの「上」との角度  {ang:.1f} 度  '
          f'{"→ 向きを共有している" if ang < 15 else "→ ★ 向きが違う。SFM と ply は別フレーム"}')
    if ang < 15:
        sign = np.sign(pn @ up)
        print(f'  ply における上  {np.round(pn*sign,4)}')
