#!/usr/bin/env python3
"""FDF の実スケールを GPS から出す。SFM の m/unit と、ply の m/unit の 2 段。

**残差で判定してはいけない。** 拘束なしの 2x3 最小二乗はアフィンなので、誤差を異方性として
吸収して「もっともらしい残差と間違ったスケール」を返す（JDL で踏んだ）。**判定は
「行ノルムが一致するか」と「行が直交するか」。** ここでは平面拘束付きの複素数フィットを使い、
相似変換しか表現できないようにする。

SFM と ply は向きを共有していることを確認済み（カメラの上と ply の最薄軸が 7.7 度）。
だから ply の m/unit は「SFM の m/unit × (ply/SFM の大きさ比)」で出る。比は、同じ幾何を
見ている 2 つの点群の**頑健な広がり**（パーセンタイル）から取る。

    python3 fdfscale.py images.txt points3D.txt gps.json scene.ply
"""
import json, re, sys
import numpy as np

IMAGES, POINTS, GPS, PLY = sys.argv[1:5]
R_EARTH = 6378137.0


def qvec2R(q):
    w, x, y, z = q
    return np.array([
        [1-2*(y*y+z*z), 2*(x*y-w*z), 2*(x*z+w*y)],
        [2*(x*y+w*z), 1-2*(x*x+z*z), 2*(y*z-w*x)],
        [2*(x*z-w*y), 2*(y*z+w*x), 1-2*(x*x+y*y)]])


# --- SFM のカメラ中心 --------------------------------------------------------
idx, C = [], []
for line in open(IMAGES):
    if line.startswith('#') or not line.strip():
        continue
    p = line.split()
    if len(p) < 10:
        continue
    R = qvec2R(np.array([float(v) for v in p[1:5]]))
    t = np.array([float(v) for v in p[5:8]])
    C.append(-R.T @ t)
    idx.append(int(re.search(r'(\d+)', p[9]).group(1)) - 1)   # image-000001 -> 0
C = np.array(C); idx = np.array(idx)
print(f'SFM カメラ {len(C):,} 台')

# --- GPS を局所 ENU メートルへ ----------------------------------------------
gps = json.load(open(GPS))
have = np.array([g is not None for g in gps])
G = np.array([g if g else [np.nan]*3 for g in gps], float)
lat0, lon0 = np.nanmean(G[:, 0]), np.nanmean(G[:, 1])
E = (G[:, 1]-lon0) * np.radians(1) * R_EARTH * np.cos(np.radians(lat0))
N = (G[:, 0]-lat0) * np.radians(1) * R_EARTH
sel = have[idx]
print(f'GPS 付き {have.sum():,} / {len(gps):,}   対応が取れたカメラ {sel.sum():,}')
print(f'  GPS の広がり  E {np.nanmax(E)-np.nanmin(E):.1f} m   N {np.nanmax(N)-np.nanmin(N):.1f} m'
      f'   高度 {np.nanmax(G[:,2])-np.nanmin(G[:,2]):.1f} m')

# --- カメラの「上」を出して、水平 2 成分を取る -------------------------------
F = []
for line in open(IMAGES):
    p = line.split()
    if line.startswith('#') or len(p) < 10:
        continue
    F.append(qvec2R(np.array([float(v) for v in p[1:5]])).T @ np.array([0, 0, 1.]))
up = -np.mean(F, 0); up /= np.linalg.norm(up)
e1 = np.cross(up, [1, 0, 0.]); e1 /= np.linalg.norm(e1)
e2 = np.cross(up, e1)
print(f'  カメラ由来の上  {np.round(up,4)}')

Cc = C[sel] - C[sel].mean(0)
P = (Cc @ e1) + 1j*(Cc @ e2)
Q = (E[idx][sel] - np.nanmean(E[idx][sel])) + 1j*(N[idx][sel] - np.nanmean(N[idx][sel]))

print(f'\n  平面拘束の相似フィット（複素数）')
best = None
for name, p in (('そのまま', P), ('鏡映', np.conj(P))):
    a = np.vdot(p, Q) / np.vdot(p, p)
    res = np.abs(a*p - Q)
    print(f'   {name:6s} スケール {abs(a):8.4f} m/unit   回転 {np.degrees(np.angle(a)):7.2f} 度'
          f'   残差 中央値 {np.median(res):.2f} m  p90 {np.percentile(res,90):.2f} m')
    if best is None or np.median(res) < best[1]:
        best = (abs(a), np.median(res), name)
sfm_scale, sfm_res, which = best
print(f'  -> SFM は {sfm_scale:.4f} m/unit（{which}、残差中央値 {sfm_res:.2f} m）')
print(f'  ** 相似変換しか許していないので、残差はそのまま誤差。異方性は入り込めない **')

# --- ply / SFM の大きさ比 ----------------------------------------------------
pts = []
with open(POINTS) as f:
    for line in f:
        if line.startswith('#') or not line.strip():
            continue
        p = line.split()
        pts.append([float(p[1]), float(p[2]), float(p[3])])
pts = np.array(pts)
print(f'\nSFM の点 {len(pts):,}')

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}
f = open(PLY, 'rb'); head = b''
while b'end_header' not in head:
    head += f.read(1 << 16)
end = head.index(b'end_header') + len(b'end_header') + 1
txt = head[:end].decode('ascii', 'replace')
n = int(re.search(r'element vertex (\d+)', txt).group(1))
dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
r = np.memmap(PLY, dtype=dt, mode='r', offset=end, shape=(n,))
op = 1/(1+np.exp(-r['opacity'].astype(np.float64)))
V = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)[op > 0.1]
print(f'ply の生存 {len(V):,}')

# 同じ幾何なので、中心からの距離のパーセンタイル比がそのまま大きさ比になる。
# **外れ値に効かないよう複数の分位で取り、一致するかを見る。**
print(f'\n  分位   SFM 半径     ply 半径     比')
ratios = []
ds = np.linalg.norm(pts - np.median(pts, 0), axis=1)
dv = np.linalg.norm(V - np.median(V, 0), axis=1)
for q in (50, 60, 70, 80, 90):
    a, b = np.percentile(ds, q), np.percentile(dv, q)
    ratios.append(b/a)
    print(f'   p{q:<3d} {a:10.4f} {b:12.4f} {b/a:9.4f}')
ratio = float(np.median(ratios))
spread = (max(ratios)-min(ratios))/ratio*100
print(f'  -> ply / SFM = {ratio:.4f}（分位間のばらつき {spread:.1f}%）')
print(f'     {"分位でよく一致。信用できる" if spread < 10 else "★ 分位でばらつく。外れ値を切ってから取り直す"}')
print(f'\n  ** ply の実スケール = {sfm_scale:.4f} / {ratio:.4f} = {sfm_scale/ratio:.4f} m/unit **')
