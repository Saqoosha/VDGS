#!/usr/bin/env python3
"""地面近くの「お椀」を、局所地面を基準に垂直方向だけ潰す。消さずに潰す。

**y=0 を地面と見なしてはいけない。** `align_ply` の床は全体の Y の 1 パーセンタイルで、
シーン全体にただ 1 枚の平面を当てているだけ。傾きは 0.15 度と小さいが、局所の起伏
（3m 離れて 0.217m）は拾えない。ここでは XZ を格子に切り、各セルで**小さい splat の
Y の低パーセンタイル**を地面とする。**大きい splat は汚染源そのものなので投票させない。**

判定に使うのは最長軸ではなく **σ_y = sqrt(Σ_yy)** —— 飛行経路に突き出す量そのもの。
横に広くて平たい splat は正しい地面板で、残す。**突き出しているものだけ潰す。**

潰し方は共分散を直接押しつぶして固有分解し直す（片方の軸を縮めるだけでは目標に届かない
—— 垂直方向の広がりは 3 軸すべての二乗和なので、1 軸を 0 にしても残りが残る）:

    M = I - (1-k) * up upᵀ,  Σ' = M Σ Mᵀ,  固有分解 -> (scale, quaternion)

四元数の並びは実測で決めた `rot_0..rot_3 = (w, x, y, z)`。

    python3 groundsquash.py in.ply                          # 測るだけ
    python3 groundsquash.py in.ply out.ply --max-sy 0.10 --band 3.0
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}


def quat_from_R(R):
    """回転行列 -> (w, x, y, z)。対角の大きい成分から取る（Shepperd）。"""
    n = len(R)
    q = np.empty((n, 4))
    t = R[:, 0, 0] + R[:, 1, 1] + R[:, 2, 2]
    a = t > 0
    s = np.sqrt(np.maximum(t[a] + 1.0, 1e-12)) * 2
    q[a, 0] = 0.25 * s
    q[a, 1] = (R[a, 2, 1] - R[a, 1, 2]) / s
    q[a, 2] = (R[a, 0, 2] - R[a, 2, 0]) / s
    q[a, 3] = (R[a, 1, 0] - R[a, 0, 1]) / s
    rest = ~a
    if rest.any():
        Rr = R[rest]
        d = np.stack([Rr[:, 0, 0], Rr[:, 1, 1], Rr[:, 2, 2]], 1)
        i = np.argmax(d, 1)
        qq = np.empty((len(Rr), 4))
        for k in (0, 1, 2):
            m = i == k
            if not m.any():
                continue
            j, l = (k + 1) % 3, (k + 2) % 3
            Rm = Rr[m]
            s = np.sqrt(np.maximum(1.0 + Rm[:, k, k] - Rm[:, j, j] - Rm[:, l, l], 1e-12)) * 2
            qq[m, 0] = (Rm[:, l, j] - Rm[:, j, l]) / s
            qq[m, 1 + k] = 0.25 * s
            qq[m, 1 + j] = (Rm[:, j, k] + Rm[:, k, j]) / s
            qq[m, 1 + l] = (Rm[:, l, k] + Rm[:, k, l]) / s
        q[rest] = qq
    q /= np.linalg.norm(q, axis=1, keepdims=True)
    q[q[:, 0] < 0] *= -1
    return q


ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst', nargs='?')
ap.add_argument('--max-sy', type=float, default=None, help='垂直方向の広がりの上限 m')
ap.add_argument('--band', type=float, default=3.0, help='地面から何 m を対象にするか')
ap.add_argument('--cell', type=float, default=2.0, help='地面格子のセル m')
ap.add_argument('--vote-size', type=float, default=0.15, help='地面に投票できる splat の最大サイズ m')
ap.add_argument('--classes', default='blob,ground',
                help='潰す形。blob=お椀 / ground=地面板 / needle=縦の針 / all=全部')
ap.add_argument('--min-size', type=float, default=None,
                help='この最長軸より大きいものだけ潰す m。**削除の代わりに使う** —— '
                     '1m 超を消したらフィールド内側の 12.6%% が黒くなった（元 0.33%%）。'
                     '寝かせれば水平方向の覆いは残り、縦の霞だけ消える')
ap.add_argument('--kill-size', type=float, default=None,
                help='この最長軸より大きいものは寝かせずに削除する m')
ap.add_argument('--all-splats', action='store_true',
                help='不透明度 0.1 以下も対象にする（検証では必ず付ける）')
ap.add_argument('--snap', choices=('zero', 'ground'), default=None,
                help='位置も動かす。zero=Y を 0 に（検証用）/ ground=局所地面に吸わせる')
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
Q = np.stack([r['rot_0'], r['rot_1'], r['rot_2'], r['rot_3']], 1).astype(np.float64)
Q /= np.maximum(np.linalg.norm(Q, axis=1, keepdims=True), 1e-12)
# 既定では不透明度 0.1 以下を「死んでいる」として触らない。**検証では必ず外す** ——
# 全体を平らにしたはずの ply に、手つかずの 424,763 個が元の高さで残っていて、
# 「潰せていない」ように見えた。
live = np.ones(n, bool) if a.all_splats else (op > 0.1)
smax = S.max(1)

# 回転行列。rot は (w, x, y, z)
w, x, y, z = Q[:, 0], Q[:, 1], Q[:, 2], Q[:, 3]
R = np.empty((n, 3, 3))
R[:, 0, 0] = 1 - 2 * (y * y + z * z); R[:, 0, 1] = 2 * (x * y - w * z); R[:, 0, 2] = 2 * (x * z + w * y)
R[:, 1, 0] = 2 * (x * y + w * z); R[:, 1, 1] = 1 - 2 * (x * x + z * z); R[:, 1, 2] = 2 * (y * z - w * x)
R[:, 2, 0] = 2 * (x * z - w * y); R[:, 2, 1] = 2 * (y * z + w * x); R[:, 2, 2] = 1 - 2 * (x * x + y * y)

# 各軸方向の広がり sqrt(diag(Sigma))
RS = R * S[:, None, :]
sig = np.sqrt((RS ** 2).sum(2))          # (n, 3) = sigma_x, sigma_y, sigma_z
sy = sig[:, 1]
sh = np.maximum(sig[:, 0], sig[:, 2])

# --- 局所地面 ---------------------------------------------------------------
vote = live & (smax < a.vote_size) & (op > 0.5)
lo = P[vote][:, [0, 2]].min(0); hi = P[vote][:, [0, 2]].max(0)
nx = int(np.ceil((hi[0] - lo[0]) / a.cell)) + 1
nz = int(np.ceil((hi[1] - lo[1]) / a.cell)) + 1
ix = np.clip(((P[:, 0] - lo[0]) / a.cell).astype(int), 0, nx - 1)
iz = np.clip(((P[:, 2] - lo[1]) / a.cell).astype(int), 0, nz - 1)
cid = ix * nz + iz
gy = np.full(nx * nz, np.nan)
vc, vy = cid[vote], P[vote, 1]
o = np.argsort(vc, kind='stable'); vc, vy = vc[o], vy[o]
bnd = np.searchsorted(vc, np.arange(nx * nz + 1))
for c in range(nx * nz):
    s, e = bnd[c], bnd[c + 1]
    if e - s >= 8:
        gy[c] = np.percentile(vy[s:e], 10)
filled = np.isnan(gy).sum()
# 空セルは近傍から埋める（反復的な平均。scipy は使わない）
G = gy.reshape(nx, nz)
for _ in range(40):
    m = np.isnan(G)
    if not m.any():
        break
    acc = np.zeros_like(G); cnt = np.zeros_like(G)
    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        Sft = np.full_like(G, np.nan)
        sl_dst = (slice(max(dx, 0), nx + min(dx, 0)), slice(max(dz, 0), nz + min(dz, 0)))
        sl_src = (slice(max(-dx, 0), nx + min(-dx, 0)), slice(max(-dz, 0), nz + min(-dz, 0)))
        Sft[sl_dst] = G[sl_src]
        ok = ~np.isnan(Sft)
        acc[ok] += Sft[ok]; cnt[ok] += 1
    fill = m & (cnt > 0)
    G[fill] = acc[fill] / cnt[fill]
G = np.nan_to_num(G, nan=np.nanmedian(G) if np.isfinite(G).any() else 0.0)
gy = G.reshape(-1)
h = P[:, 1] - gy[cid]

print(f'{a.src.split("/")[-1]}  生存 {live.sum():,}')
print(f'  地面格子 {nx}x{nz}（{a.cell} m）  投票した splat {vote.sum():,}'
      f'  最初に埋まったセル {nx*nz-filled:,}/{nx*nz:,}')
gmin, gmax = np.nanmin(G), np.nanmax(G)
print(f'  局所地面の高さ  最小 {gmin:.2f}  p50 {np.median(G):.2f}  最大 {gmax:.2f} m'
      f'  ← 平面 1 枚なら 0 のはず。起伏 {gmax-gmin:.2f} m')

# --band を 0 以下にすると帯を外して**シーン全体**を対象にする。アルゴリズムの検証用:
# 全 splat の σ_y を 0 に潰して、横から見て平らな薄片の集まりになれば、ワールド Y で
# 潰せている証拠になる。
band = live.copy() if a.band <= 0 else (live & (h > -0.5) & (h < a.band))
print(f'\n  地面から {a.band} m 以内の生存 {band.sum():,}')
print(f'  σ_y (m)  p50 {np.percentile(sy[band],50):.3f}  p90 {np.percentile(sy[band],90):.3f}'
      f'  p99 {np.percentile(sy[band],99):.3f}  最大 {sy[band].max():.2f}')

# --- 形で仕分ける -----------------------------------------------------------
# **σ_y だけで潰すとゲートの旗もポールも木も潰れる。** 縦に長いのが正しいものが
# 帯の中に居る。潰していいのは「本物の幾何ではありえない形」だけ:
#   お椀   —— 3 軸がほぼ等しい球。滑らかな面をもつ 30cm の球という物体は存在しない
#   地面板 —— いちばん薄い軸が上を向いている板。厚いなら潰していい
#   縦の針 —— 上を向いた線。草の artifact
# 逃がすのは**立った板**（ゲート・旗・幹・人）と**寝た針**。
ordS = np.argsort(S, 1)                       # 昇順: min, mid, max
smin = np.take_along_axis(S, ordS[:, :1], 1)[:, 0]
smid = np.take_along_axis(S, ordS[:, 1:2], 1)[:, 0]
axmin = np.take_along_axis(R, ordS[:, None, :1], 2)[:, :, 0]   # 最小軸の向き＝法線
axmax = np.take_along_axis(R, ordS[:, None, 2:], 2)[:, :, 0]   # 最長軸の向き
vmin = np.abs(axmin[:, 1])                    # 法線がどれだけ上を向いているか
vmax = np.abs(axmax[:, 1])
needle = smid / np.maximum(smax, 1e-9) < 0.5
blob = (~needle) & (smin / np.maximum(smax, 1e-9) > 0.5)
plate = ~needle & ~blob
cls = {
    'お椀（ほぼ球）': blob,
    '地面板（法線が上）': plate & (vmin > 0.7),
    '立った板（ゲート等）': plate & (vmin <= 0.7),
    '縦の針': needle & (vmax > 0.5),
    '寝た針': needle & (vmax <= 0.5),
}
print(f'\n  形の内訳（地面帯の中）        個数      帯内比   σ_y p50   σ_y p99   高さ p50')
for nm, m in cls.items():
    k = band & m
    if k.sum() == 0:
        continue
    print(f'   {nm:20s} {k.sum():10,} {k.sum()/band.sum()*100:7.2f}% '
          f'{np.percentile(sy[k],50):9.3f} {np.percentile(sy[k],99):9.3f} '
          f'{np.median(h[k]):9.2f}')

SETS = {'blob': blob, 'ground': plate & (vmin > 0.7), 'needle': needle & (vmax > 0.5),
        'all': np.ones(n, bool)}
squashable = np.zeros(n, bool)
for nm in a.classes.split(','):
    squashable |= SETS[nm.strip()]
print(f'\n  潰してよい形だけに絞った場合')
print(f'  σ_y 上限   対象個数   帯内比    最長軸中央値  σ_h 中央値  高さ中央値  縦横比')
for th in (0.30, 0.20, 0.15, 0.10, 0.07, 0.05):
    k = band & squashable & (sy > th)
    if k.sum() == 0:
        continue
    print(f'  {th:5.2f} m {k.sum():10,} {k.sum()/band.sum()*100:7.2f}% '
          f'{np.median(smax[k]):11.3f} {np.median(sh[k]):11.3f} '
          f'{np.median(h[k]):11.2f} {np.median(sy[k]/np.maximum(sh[k],1e-6)):8.2f}')
print(f'  （逃がした「立った板」で σ_y > 0.10 は '
      f'{int((band & plate & (vmin<=0.7) & (sy>0.10)).sum()):,} 個）')

if a.max_sy is None or not a.dst:
    raise SystemExit

hit = band & squashable & (sy > a.max_sy)
if a.min_size is not None:
    hit &= smax > a.min_size
    print(f'  最長軸 {a.min_size} m 超に限定: {hit.sum():,}')
print(f'\n潰す {hit.sum():,}（帯内の {hit.sum()/band.sum()*100:.2f}%、全体の {hit.sum()/n*100:.3f}%）')
up = np.array([0.0, 1.0, 0.0])
k = np.clip(a.max_sy / sy[hit], 1e-4, 1.0)[:, None, None]
M = np.eye(3)[None] - (1 - k) * np.outer(up, up)[None]
Rh, Sh_ = R[hit], S[hit]
Sig = np.einsum('nij,nj,nkj->nik', Rh, Sh_ ** 2, Rh)
Sig = np.einsum('nij,njk,nlk->nil', M, Sig, M)
lam, V = np.linalg.eigh(Sig)
lam = np.maximum(lam, 1e-12)
V[np.linalg.det(V) < 0, :, 0] *= -1
S[hit] = np.sqrt(lam)
Q[hit] = quat_from_R(V)

# 検算: 潰したあとの sigma_y
w2, x2, y2, z2 = Q[hit, 0], Q[hit, 1], Q[hit, 2], Q[hit, 3]
m = len(w2)
R2 = np.empty((m, 3, 3))
R2[:, 0, 0] = 1 - 2*(y2*y2 + z2*z2); R2[:, 0, 1] = 2*(x2*y2 - w2*z2); R2[:, 0, 2] = 2*(x2*z2 + w2*y2)
R2[:, 1, 0] = 2*(x2*y2 + w2*z2); R2[:, 1, 1] = 1 - 2*(x2*x2 + z2*z2); R2[:, 1, 2] = 2*(y2*z2 - w2*x2)
R2[:, 2, 0] = 2*(x2*z2 - w2*y2); R2[:, 2, 1] = 2*(y2*z2 + w2*x2); R2[:, 2, 2] = 1 - 2*(x2*x2 + y2*y2)
sy2 = np.sqrt(((R2 * S[hit][:, None, :]) ** 2).sum(2))[:, 1]
print(f'  検算 σ_y  前 p50 {np.median(sy[hit]):.4f} 最大 {sy[hit].max():.3f}'
      f'  ->  後 p50 {np.median(sy2):.4f} 最大 {sy2.max():.4f}  （目標 {a.max_sy}）')

out = r.copy()
for i in range(3):
    out[f'scale_{i}'] = np.log(S[:, i]).astype(np.float32)
for i in range(4):
    out[f'rot_{i}'] = Q[:, i].astype(np.float32)
if a.snap:
    ny = np.zeros(hit.sum()) if a.snap == 'zero' else gy[cid[hit]]
    print(f'  位置を {a.snap} に吸わせる: Y  前 p1 {np.percentile(P[hit,1],1):.2f}'
          f' p50 {np.median(P[hit,1]):.2f} p99 {np.percentile(P[hit,1],99):.2f}'
          f'  ->  後 p1 {np.percentile(ny,1):.2f} p50 {np.median(ny):.2f}'
          f' p99 {np.percentile(ny,99):.2f}')
    yy = P[:, 1].copy(); yy[hit] = ny
    out['y'] = yy.astype(np.float32)
keep = np.ones(n, bool)
if a.kill_size is not None:
    keep = smax <= a.kill_size
    print(f'  最長軸 {a.kill_size} m 超は削除: {int((~keep).sum()):,}'
          f'（うち生存 {int((~keep & live).sum()):,}）')
    out = out[keep]
hdr = re.sub(r'element vertex \d+', f'element vertex {len(out)}',
             txt[:txt.index('end_header')])
with open(a.dst, 'wb') as gf:
    gf.write(hdr.encode('ascii')); gf.write(b'end_header\n'); gf.write(out.tobytes())
print(f'wrote {a.dst}  {len(out):,} splats')
