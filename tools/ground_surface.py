#!/usr/bin/env python3
"""splat の地面を、真上から見た高さ地図にする。コリジョンの地面の正解として使う。

    python3 tools/ground_surface.py field.ply out.npz --cell 0.1 [--band 0.5]

各セルの高さは「真上から積んだ不透明度が 0.5 を越える高さ」。目に見える面そのもので、
芝なら芝の上、土なら土。splat の中心の分位点ではない（中心は面の上下に散る）。

描画は正射影で、ガウシアンをセルの箱で畳み込んでから（分散に cell²/12 を足し、
振幅を面積比で下げる）セル中心で評価する。小さい splat がセル中心から外れて消える
エイリアスを避けるため。正射影で描いてよいのは、共分散を正射影のヤコビアンで落とすから
（透視の式を流用していない）。

ただし小さい splat が密に並んだ地面は、上から積むと「すかすか」で 0.5 に届かず、面が層の
下のごみまで沈む（ゲート 24 の周りで 5〜40 cm）。そこで、帯の splat の不透明度重み付き
パーセンタイル（--layer-pct）を下限にして、それより深くは沈ませない。

ゲートの柱や木を地面に数えないよう、粗い局所地面（1 m セル）から ±band の帯に入る
splat だけを使う。ply は Y-down なので「上」は y の小さい側。

出力 npz: h（セルの面の y、無いセルは nan）、alpha（積んだ不透明度）、x0, z0, cell。
h[i, j] はセル中心 (x0 + (j+0.5)cell, z0 + (i+0.5)cell)。
"""
import argparse, re
import numpy as np
from scipy import ndimage

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4', 'uint': '<u4'}


def read_ply(path):
    f = open(path, 'rb'); head = b''
    while b'end_header' not in head:
        head += f.read(1 << 16)
    end = head.index(b'end_header') + len(b'end_header') + 1
    txt = head[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', txt).group(1))
    dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
    return np.fromfile(path, dtype=dt, offset=end, count=n)


def quat2R(q):
    w, x, y, z = q.T; R = np.empty((len(q), 3, 3))
    R[:, 0, 0] = 1 - 2*(y*y + z*z); R[:, 0, 1] = 2*(x*y - w*z); R[:, 0, 2] = 2*(x*z + w*y)
    R[:, 1, 0] = 2*(x*y + w*z); R[:, 1, 1] = 1 - 2*(x*x + z*z); R[:, 1, 2] = 2*(y*z - w*x)
    R[:, 2, 0] = 2*(x*z - w*y); R[:, 2, 1] = 2*(y*z + w*x); R[:, 2, 2] = 1 - 2*(x*x + y*y)
    return R


def coarse_ground(P, op, smax, x0, z0, W, H, cell=1.0, dy=0.05):
    """1 m セルの粗い地面 y。セルごとに、小さく濃い splat の不透明度を高さ 5 cm 刻みで
    積み、いちばん重い高さを取る（最頻値）。地面はそのセルで最も濃い層なので、地面の下に
    浮いたごみにも、柱や樹冠にも引っぱられない。

    以前は「低い側の 85 パーセンタイル」に最大値フィルタ（低い側へ寄せる）を重ねていて、
    地面の下のごみに引っぱられた粗い地面が本物の地面を ±band の外へ押し出し、ゲート 24 の
    周りで面が 5〜40 cm 沈んでいた。"""
    k = (op > 0.3) & (smax < 0.2)
    ix = ((P[k, 0] - x0) / cell).astype(int); iz = ((P[k, 2] - z0) / cell).astype(int)
    ok = (ix >= 0) & (ix < W) & (iz >= 0) & (iz < H)
    y = P[k, 1][ok]; wgt = op[k][ok]; key = iz[ok] * W + ix[ok]
    y0 = y.min(); nb = int((y.max() - y0) / dy) + 1
    b = ((y - y0) / dy).astype(int)
    hist = np.bincount(key * nb + b, weights=wgt, minlength=H * W * nb).reshape(H * W, nb)
    hist = ndimage.uniform_filter1d(hist, 3, axis=1)     # 隣の刻みと均して、境目で割れないように
    g = y0 + (hist.argmax(1) + 0.5) * dy
    g[hist.max(1) <= 0] = np.nan
    g = g.reshape(H, W)
    filled = np.where(np.isnan(g), np.nanmedian(g), g)
    return ndimage.median_filter(filled, size=3)


def layer_bottom(P, op, x0, z0, W, H, cell, pct):
    """セルごとに、帯の中の splat の y を不透明度で重み付けした pct パーセンタイル
    （Y-down なので層の下側）。小さい splat が密に並んだ「すかすか」の地面でも、
    層そのものの位置は重みが教えてくれる。"""
    ix = ((P[:, 0] - x0) / cell).astype(int); iz = ((P[:, 2] - z0) / cell).astype(int)
    ok = (ix >= 0) & (ix < W) & (iz >= 0) & (iz < H)
    key = (iz * W + ix)[ok]; y = P[ok, 1]; w = op[ok]
    o = np.lexsort((y, key)); key, y, w = key[o], y[o], w[o]
    starts = np.r_[0, np.flatnonzero(np.diff(key)) + 1]; n = np.diff(np.r_[starts, len(key)])
    cw = np.cumsum(w); base = np.repeat(cw[starts] - w[starts], n); tot = np.repeat(np.add.reduceat(w, starts), n)
    frac = (cw - base) / tot
    seg = np.repeat(np.arange(len(starts)), n)
    hit = np.flatnonzero(frac >= pct / 100)
    first = np.full(len(starts), len(key)); np.minimum.at(first, seg[hit], hit)
    out = np.full(H * W, np.nan)
    good = (first < len(key)) & (n >= 5)
    out[key[starts[good]]] = y[first[good]]
    return out.reshape(H, W)


def render_top(P, Sig, a, x0, z0, W, H, cell, max_r=40, thr=0.5):
    """真上からの正射影。各セルで、上から積んだ不透明度が thr を越える y を返す。"""
    c = np.stack([np.stack([Sig[:, 0, 0], Sig[:, 0, 2]], 1),
                  np.stack([Sig[:, 2, 0], Sig[:, 2, 2]], 1)], 1) / cell**2
    det0 = np.maximum(c[:, 0, 0] * c[:, 1, 1] - c[:, 0, 1]**2, 1e-12)
    c[:, 0, 0] += 1 / 12; c[:, 1, 1] += 1 / 12        # セルの箱で畳み込む
    det = c[:, 0, 0] * c[:, 1, 1] - c[:, 0, 1]**2
    amp = a * np.sqrt(det0 / det)                       # 面積を保つ
    inv = np.stack([c[:, 1, 1], -c[:, 0, 1], c[:, 0, 0]], 1) / det[:, None]
    X = (P[:, 0] - x0) / cell; Z = (P[:, 2] - z0) / cell
    rad = np.minimum(np.ceil(3 * np.sqrt(np.maximum(c[:, 0, 0], c[:, 1, 1]))), max_r).astype(int)
    pix, dep, al = [], [], []
    for r in np.unique(rad):
        s = np.flatnonzero(rad == r)
        for chunk in np.array_split(s, max(1, len(s) * (2*r+1)**2 // 20_000_000 + 1)):
            off = np.arange(-r, r + 1)
            gx, gz = np.meshgrid(off, off); gx = gx.ravel(); gz = gz.ravel()
            cx = np.floor(X[chunk]).astype(int)[:, None] + gx
            cz = np.floor(Z[chunk]).astype(int)[:, None] + gz
            dx = cx + 0.5 - X[chunk][:, None]; dz = cz + 0.5 - Z[chunk][:, None]
            iv = inv[chunk]
            m = iv[:, [0]] * dx * dx + 2 * iv[:, [1]] * dx * dz + iv[:, [2]] * dz * dz
            A = np.minimum(0.99, amp[chunk][:, None] * np.exp(-0.5 * m))
            ok = (A >= 1 / 255) & (cx >= 0) & (cx < W) & (cz >= 0) & (cz < H)
            pix.append((cz * W + cx)[ok]); al.append(A[ok])
            dep.append(np.broadcast_to(P[chunk, 1][:, None], A.shape)[ok])
    pix = np.concatenate(pix); dep = np.concatenate(dep); al = np.concatenate(al)
    o = np.lexsort((dep, pix)); pix, dep, al = pix[o], dep[o], al[o]
    logT = np.log1p(-al)
    starts = np.r_[0, np.flatnonzero(np.diff(pix)) + 1]
    cum = np.cumsum(logT)
    base = np.repeat(cum[starts] - logT[starts], np.diff(np.r_[starts, len(pix)]))
    T = np.exp(cum - base)                               # セルごとに上から積んだ透過率
    crossed = T <= 1 - thr
    h = np.full(H * W, np.nan); opa = np.zeros(H * W)
    seg = np.repeat(np.arange(len(starts)), np.diff(np.r_[starts, len(pix)]))
    idx = np.flatnonzero(crossed)
    # 各セルで最初に thr を越えた fragment
    fi = np.full(len(starts), len(pix))
    np.minimum.at(fi, seg[idx], idx)
    has = fi < len(pix)
    h[pix[starts[has]]] = dep[fi[has]]
    ends = np.r_[starts[1:], len(pix)] - 1
    opa[pix[starts]] = 1 - T[ends]
    return h.reshape(H, W), opa.reshape(H, W)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('src'); ap.add_argument('dst')
    ap.add_argument('--cell', type=float, default=0.1)
    ap.add_argument('--band', type=float, default=0.5, help='粗い地面から上下この幅だけ使う m')
    ap.add_argument('--threshold', type=float, default=0.5, help='面とみなす積算不透明度')
    ap.add_argument('--bounds', help='x0,z0,x1,z1（既定は入力の範囲）')
    ap.add_argument('--layer-pct', type=float, default=85,
                    help='面がこれより深く沈まない下限：帯の splat の y の、不透明度重み付きパーセンタイル。'
                         '0 で無効')
    a = ap.parse_args()

    r = read_ply(a.src)
    P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
    op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
    S = np.exp(np.stack([r[f'scale_{i}'] for i in range(3)], 1).astype(np.float64))
    if a.bounds:
        x0, z0, x1, z1 = map(float, a.bounds.split(','))
    else:
        x0, z0 = P[:, 0].min(), P[:, 2].min(); x1, z1 = P[:, 0].max(), P[:, 2].max()

    Wc, Hc = int(np.ceil((x1 - x0))), int(np.ceil((z1 - z0)))
    g = coarse_ground(P, op, S.max(1), x0, z0, Wc, Hc)
    gi = np.clip(((P[:, 2] - z0)).astype(int), 0, Hc - 1); gj = np.clip(((P[:, 0] - x0)).astype(int), 0, Wc - 1)
    near = np.abs(P[:, 1] - g[gi, gj]) < a.band
    print(f'{len(P):,} splats, {near.sum():,} in the ground band (±{a.band} m)')

    q = np.stack([r[f'rot_{i}'] for i in range(4)], 1).astype(np.float64)[near]
    q /= np.linalg.norm(q, axis=1, keepdims=True)
    R = quat2R(q); Sig = np.einsum('nij,nj,nkj->nik', R, S[near]**2, R)
    W, H = int(np.ceil((x1 - x0) / a.cell)), int(np.ceil((z1 - z0) / a.cell))
    h, opa = render_top(P[near], Sig, op[near], x0, z0, W, H, a.cell, thr=a.threshold)
    print(f'grid {W}x{H} @ {a.cell} m: surface in {np.isfinite(h).mean()*100:.1f}% of cells')
    h_alpha = h
    if a.layer_pct > 0:
        hb = layer_bottom(P[near], op[near], x0, z0, W, H, a.cell, a.layer_pct)
        h = np.fmin(h_alpha, hb)        # Y-down: 小さいほうが上。層の底より深く沈ませない
        moved = np.isfinite(h_alpha) & (h < h_alpha - 1e-6)
        print(f'layer floor: raised {moved.mean()*100:.1f}% of cells, filled {(~np.isfinite(h_alpha) & np.isfinite(h)).mean()*100:.1f}%; '
              f'surface now in {np.isfinite(h).mean()*100:.1f}% of cells')
    np.savez_compressed(a.dst, h=h, h_alpha=h_alpha, alpha=opa, x0=x0, z0=z0, cell=a.cell, coarse=g)


if __name__ == '__main__':
    main()
