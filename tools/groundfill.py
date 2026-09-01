#!/usr/bin/env python3
"""**コリジョン用の** ply に、地面の穴を合成 splat で埋める。描画用ではない。

**見た目用に合成 splat を作ってはいけない。** FDF で一度やって霞とチラつきが戻った。
見た目の穴は実在 splat の複製でしか埋めない。

**コリジョン用は話が別。** この ply は密度場を焼くための入力で、**一度も描画されない**。
だから中身は何でもよく、体積さえあればいい。

実測（topcover.py）: 元の AirVis 出力の時点で、地面帯の 2m セルの **62%** が
「splat の面積の和 < セル面積」。削除のせいではなく撮影と再構成の側の穴。

地面の高さは XZ 格子で、小さく濃い splat の Y の 10 パーセンタイルから取る。**splat が
1 個も無いセルでも近傍からの拡散で埋まっている**ので、そこに平たい円盤を置けば地形に沿う。

    python3 groundfill.py in.ply out.ply --cover 1.0 --disc 0.25
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst')
ap.add_argument('--cell', type=float, default=1.0, help='被覆を測る格子 m')
ap.add_argument('--gcell', type=float, default=2.0, help='地面の高さの格子 m')
ap.add_argument('--band', type=float, default=3.0)
ap.add_argument('--cover', type=float, default=1.0, help='これ未満の被覆のセルを埋める')
ap.add_argument('--disc', type=float, default=0.25, help='合成円盤の半径 m')
ap.add_argument('--thick', type=float, default=0.12, help='合成円盤の厚み m（＝格子 1 セル）')
ap.add_argument('--per-cell', type=int, default=4, help='1 セルに置く数（ずらして配置）')
ap.add_argument('--drop-flat', type=float, default=None,
                help='この大きさ超の「平たくて地面から浮いている」splat を落とす m。'
                     '寝かせた大きい splat は見た目には要るが、**コリジョンでは空中に'
                     '浮いた見えない板**になる。地面は合成円盤で作るので要らない')
ap.add_argument('--drop-above', type=float, default=1.0, help='--drop-flat の高さ下限 m')
ap.add_argument('--ground-erode', type=int, default=3,
                help='地面推定にかける最小値フィルタの回数（セル単位の半径）')
ap.add_argument('--max-seed-dist', type=int, default=3,
                help='実測セルからこのセル数までしか埋めない。**遠くまで拡散した地面推定は'
                     '当てにならない** —— 木の周りでは「木の低い方の Y」を地面と誤認し、'
                     'そこから拡散すると 4m 空中に円盤を置いてしまう')
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
S = np.sort(np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1)
                   .astype(np.float64)), 1)
live = op > 0.1
smax = S[:, 2]

# --- 局所地面 ---------------------------------------------------------------
vote = live & (smax < 0.15) & (op > 0.5)
lo = P[vote][:, [0, 2]].min(0); hi = P[vote][:, [0, 2]].max(0)
gx = int(np.ceil((hi[0]-lo[0])/a.gcell))+1; gz = int(np.ceil((hi[1]-lo[1])/a.gcell))+1
gi = np.clip(((P[:, 0]-lo[0])/a.gcell).astype(int), 0, gx-1)
gk = np.clip(((P[:, 2]-lo[1])/a.gcell).astype(int), 0, gz-1)
gid = gi*gz + gk
gy = np.full(gx*gz, np.nan)
vc, vy = gid[vote], P[vote, 1]
o = np.argsort(vc, kind='stable'); vc, vy = vc[o], vy[o]
bnd = np.searchsorted(vc, np.arange(gx*gz+1))
for c in range(gx*gz):
    s, e = bnd[c], bnd[c+1]
    if e-s >= 8:
        gy[c] = np.percentile(vy[s:e], 10)
seeded = np.isfinite(gy).sum()
G = gy.reshape(gx, gz)
for _ in range(60):
    m = np.isnan(G)
    if not m.any():
        break
    acc = np.zeros_like(G); cnt = np.zeros_like(G)
    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        Sft = np.full_like(G, np.nan)
        Sft[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
            G[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
        ok = ~np.isnan(Sft); acc[ok] += Sft[ok]; cnt[ok] += 1
    fl = m & (cnt > 0); G[fl] = acc[fl]/cnt[fl]
G = np.nan_to_num(G, nan=float(np.nanmedian(gy)) if seeded else 0.0)

# **木のセルは地面を高く見積もる。** 樹冠で埋まったセルでは「小さく濃い splat の Y の
# 10 パーセンタイル」が樹冠の底になり、そこに円盤を置くと **4m 空中の見えない壁**になる。
# 最小値フィルタで周囲の本物の地面まで引き下げる。**下げすぎは埋まるだけで無害、
# 上げすぎは見えない壁** —— 非対称なので下げる側に倒す。
if a.ground_erode > 0:
    before = G.copy()
    E = G.copy()
    for _ in range(a.ground_erode):
        nb = E.copy()
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            Sft = np.full_like(E, np.inf)
            Sft[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
                E[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
            nb = np.minimum(nb, Sft)
        E = nb
    for _ in range(2):                      # 段差をならす
        acc = E.copy(); cnt = np.ones_like(E)
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            Sft = np.full_like(E, np.nan)
            Sft[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
                E[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
            ok2 = ~np.isnan(Sft); acc[ok2] += Sft[ok2]; cnt[ok2] += 1
        E = acc/cnt
    print(f'  地面推定に最小値フィルタ {a.ground_erode} 回: 高さ p50 {np.median(before):.2f}'
          f' -> {np.median(E):.2f}   p99 {np.percentile(before,99):.2f}'
          f' -> {np.percentile(E,99):.2f} m')
    G = E
gflat = G.reshape(-1)
h = P[:, 1] - gflat[gid]
band = live & (h > -1.0) & (h < a.band)

# 実測セルからの距離（セル単位）。遠いところの地面推定は信用しない
seedmap = np.isfinite(gy).reshape(gx, gz)
dist = np.where(seedmap, 0, 10**6).astype(np.int32)
for _ in range(a.max_seed_dist + 1):
    nb = dist.copy()
    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        Sft = np.full_like(dist, 10**6)
        Sft[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
            dist[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
        nb = np.minimum(nb, Sft + 1)
    dist = nb
trust = (dist <= a.max_seed_dist).reshape(-1)
print(f'  地面推定を信用するセル {trust.sum():,} / {gx*gz:,}'
      f'（実測 {seeded:,} から {a.max_seed_dist} セル以内）')

# --- 被覆 -------------------------------------------------------------------
x0, x1 = np.percentile(P[live, 0], [0.5, 99.5])
z0, z1 = np.percentile(P[live, 2], [0.5, 99.5])
nx = int(np.ceil((x1-x0)/a.cell)); nz = int(np.ceil((z1-z0)/a.cell))
ix = ((P[:, 0]-x0)/a.cell).astype(int); iz = ((P[:, 2]-z0)/a.cell).astype(int)
ok = band & (ix >= 0) & (ix < nx) & (iz >= 0) & (iz < nz)
area = S[:, 2]*S[:, 1]*np.pi
C = np.bincount(ix[ok]*nz+iz[ok], weights=area[ok]*op[ok],
                minlength=nx*nz).reshape(nx, nz)/(a.cell**2)
need = C < a.cover
# 信用できない地面推定のセルには置かない
qi_all = np.clip(((x0 + (np.arange(nx)+0.5)*a.cell - lo[0])/a.gcell).astype(int), 0, gx-1)
qk_all = np.clip(((z0 + (np.arange(nz)+0.5)*a.cell - lo[1])/a.gcell).astype(int), 0, gz-1)
need &= trust[qi_all[:, None]*gz + qk_all[None, :]]
print(f'{a.src.split("/")[-1]}  {n:,} splats  地面格子 {gx}x{gz}（実測セル {seeded:,}）')
print(f'  被覆格子 {nx}x{nz}（{a.cell} m）  被覆 p50 {np.median(C):.2f}')
print(f'  埋めるセル {need.sum():,} / {C.size:,} ({need.mean()*100:.1f}%)')

# --- 合成円盤 ---------------------------------------------------------------
ci, ck = np.nonzero(need)
rng = np.random.default_rng(0)
m = len(ci)*a.per_cell
cx = np.repeat(x0 + (ci+0.5)*a.cell, a.per_cell) + rng.uniform(-a.cell/2, a.cell/2, m)
cz = np.repeat(z0 + (ck+0.5)*a.cell, a.per_cell) + rng.uniform(-a.cell/2, a.cell/2, m)
qi = np.clip(((cx-lo[0])/a.gcell).astype(int), 0, gx-1)
qk = np.clip(((cz-lo[1])/a.gcell).astype(int), 0, gz-1)
cy = gflat[qi*gz+qk]
print(f'  置く合成 splat {m:,}（半径 {a.disc} m、厚み {a.thick} m、1 セルに {a.per_cell} 個）')
print(f'  合成の高さ  p1 {np.percentile(cy,1):.2f}  p50 {np.median(cy):.2f}'
      f'  p99 {np.percentile(cy,99):.2f} m')

add = np.zeros(m, dtype=dt)
add['x'] = cx.astype(np.float32); add['y'] = cy.astype(np.float32); add['z'] = cz.astype(np.float32)
add['opacity'] = np.float32(4.0)                       # sigmoid(4) = 0.982
add['scale_0'] = np.float32(np.log(a.disc))
add['scale_1'] = np.float32(np.log(a.thick))
add['scale_2'] = np.float32(np.log(a.disc))
add['rot_0'] = np.float32(1.0)                         # (w,x,y,z) = 単位回転。薄い軸が +Y
for nm in ('rot_1', 'rot_2', 'rot_3'):
    add[nm] = np.float32(0.0)
# --- 色はセルごとに、その場所の実際の地面 splat から取る ---------------------
# **帯 3m の中央値を 1 色使うのは間違い。** その帯には草も低木も入るので緑に引かれ、
# 茶色い地面に緑の円盤が並んだ。高さと同じ格子・同じ拡散で、地表の色を局所に取る。
C0 = 0.28209479177387814
surf = live & (np.abs(h) < 0.3) & (op > 0.5)      # 地表そのもの。草の丈より低く取る
print(f'  色を取る地表 splat {surf.sum():,}')
for i in range(3):
    key = f'f_dc_{i}'
    if key not in dt.names:
        continue
    val = r[key][surf].astype(np.float64)
    cid_s = gid[surf]
    o2 = np.argsort(cid_s, kind='stable')
    cs, vs = cid_s[o2], val[o2]
    b2 = np.searchsorted(cs, np.arange(gx*gz+1))
    Cg = np.full(gx*gz, np.nan)
    for c in range(gx*gz):
        s, e = b2[c], b2[c+1]
        if e-s >= 4:
            Cg[c] = np.median(vs[s:e])
    Cm = Cg.reshape(gx, gz)
    for _ in range(60):
        mm = np.isnan(Cm)
        if not mm.any():
            break
        acc = np.zeros_like(Cm); cnt = np.zeros_like(Cm)
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            Sft = np.full_like(Cm, np.nan)
            Sft[max(dx, 0):gx+min(dx, 0), max(dz, 0):gz+min(dz, 0)] = \
                Cm[max(-dx, 0):gx+min(-dx, 0), max(-dz, 0):gz+min(-dz, 0)]
            okc = ~np.isnan(Sft); acc[okc] += Sft[okc]; cnt[okc] += 1
        fl = mm & (cnt > 0); Cm[fl] = acc[fl]/cnt[fl]
    Cm = np.nan_to_num(Cm, nan=float(np.nanmedian(Cg)) if np.isfinite(Cg).any() else 0.0)
    add[key] = Cm.reshape(-1)[qi*gz+qk].astype(np.float32)
rgb = np.clip(np.stack([add[f'f_dc_{i}'] for i in range(3)], 1).astype(np.float64)
              * C0 + 0.5, 0, 1)
print(f'  合成の色 RGB  中央値 {np.median(rgb[:,0]):.2f}/{np.median(rgb[:,1]):.2f}/'
      f'{np.median(rgb[:,2]):.2f}  （茶色なら R>G>B）')

keep = np.ones(n, bool)
if a.drop_flat is not None:
    # **寝かせた大きい splat は、コリジョンでは空中に浮いた見えない板になる。**
    # 見た目には要るのでこちらの ply からだけ落とす。σ_y はワールド系で測る
    # （ローカルのスケールでは向きが分からない）。
    Q = np.stack([r['rot_0'], r['rot_1'], r['rot_2'], r['rot_3']], 1).astype(np.float64)
    Q /= np.maximum(np.linalg.norm(Q, axis=1, keepdims=True), 1e-12)
    w_, x_, y_, z_ = Q[:, 0], Q[:, 1], Q[:, 2], Q[:, 3]
    Sf = np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1).astype(np.float64))
    R1 = np.stack([2*(x_*y_ + w_*z_), 1 - 2*(x_*x_ + z_*z_), 2*(y_*z_ - w_*x_)], 1)
    sy = np.sqrt(((R1 * Sf) ** 2).sum(1))          # sqrt(Sigma_yy)
    flat_high = (smax > a.drop_flat) & (sy < 0.1) & (h > a.drop_above)
    keep = ~flat_high
    print(f'  空中の平たい大 splat を落とす {int(flat_high.sum()):,}'
          f'（{a.drop_flat} m 超・σ_y<0.1・地面から {a.drop_above} m 超）')
    if flat_high.any():
        print(f'    落とすものの高さ  p50 {np.median(h[flat_high]):.2f}'
              f'  p99 {np.percentile(h[flat_high],99):.2f} m'
              f'   大きさ p50 {np.median(smax[flat_high]):.2f} m')
out = np.concatenate([r[keep], add])
hdr = re.sub(r'element vertex \d+', f'element vertex {len(out)}', txt[:txt.index('end_header')])
with open(a.dst, 'wb') as g:
    g.write(hdr.encode('ascii')); g.write(b'end_header\n'); g.write(out.tobytes())
print(f'wrote {a.dst}  {len(out):,} splats（元 {n:,} ＋ 合成 {m:,}）')
