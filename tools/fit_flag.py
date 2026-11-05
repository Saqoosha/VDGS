#!/usr/bin/env python3
"""旗の布を、水平なカプセルの積み重ねに当てる。

    python3 tools/fit_flag.py cloth.ply out.parts.json --ground-y <y> --cloth-y <y> [--no-pole]

布は直線ではないので fit_parts.py の RANSAC には乗らない（旗 1 本で 8 本の斜めの線になり、
点の半分が余る）。ここでは高さ --dy ごとの段に切り、各段の点を真上から見た弦（重み付き PCA の
第 1 軸、垂直方向は 4 MAD で外れ値を落とす）に 1 本のカプセルを当てる。半径は弦からの距離の
--pct パーセンタイル ＋ --pad。先端は点が少なすぎて向きが決まらないので、上下の端の中央値を
結ぶ 1 本にし、半径は 12 cm で頭打ち（ぼやけた旗で 26 cm になった）。

--no-pole のとき布と先端だけを出す。ポールは骨組みとして fit_parts.py に渡すほうがよく、
fit_scene_parts.py はそうしている。入力は ply 座標（Y-down）。出力も同じで、鏡映は
collision_pack.py かランタイムが掛ける。
"""
import argparse, json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fit_parts import read_ply, seg_dist, capsule_mesh, write_mesh_ply


def wpca(Q, ww):
    c = np.average(Q, axis=0, weights=ww)
    u = np.linalg.svd((Q - c) * np.sqrt(ww)[:, None], full_matrices=False)[2][0]
    return c, u


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('src'); ap.add_argument('dst')
    ap.add_argument('--ground-y', type=float, required=True, help='これより下（y が大きい側）は捨てる')
    ap.add_argument('--cloth-y', type=float, required=True, help='布の下端の y（上は小さい側）')
    ap.add_argument('--base-y', type=float, help='ポールの根元の y（既定は --ground-y）')
    ap.add_argument('--dy', type=float, default=0.1, help='段の高さ m')
    ap.add_argument('--pad', type=float, default=0.025)
    ap.add_argument('--pct', type=float, default=75)
    ap.add_argument('--min-r', type=float, default=0.0)
    ap.add_argument('--mesh', help='確認用の三角形 ply')
    ap.add_argument('--no-pole', action='store_true', help='布と先端だけ')
    a = ap.parse_args()

    r = read_ply(a.src)
    P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
    op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
    smid = np.sort(np.exp(np.stack([r[f'scale_{i}'] for i in range(3)], 1).astype(np.float64)), 1)[:, 1]
    k = (op >= 0.25) & (smid <= 0.05) & (P[:, 1] < a.ground_y)
    P, w = P[k], op[k]

    caps = []
    if not a.no_pole:
        # ポール：布の下端から地面までの点。中心の 10 cm 以内に絞ってから軸を引く
        pm = (P[:, 1] > a.cloth_y) & (P[:, 1] < a.ground_y - 0.15)
        mxz = np.median(P[pm][:, [0, 2]], axis=0)
        pm &= np.linalg.norm(P[:, [0, 2]] - mxz, axis=1) < 0.1
        c, u = wpca(P[pm], w[pm])
        if u[1] < 0: u = -u                      # 下向き（+y）
        d, _ = seg_dist(P[pm], c - u, c + u)
        keep = d < 3 * np.median(d) + 0.01
        c, u = wpca(P[pm][keep], w[pm][keep]); u = u if u[1] > 0 else -u
        rp = float(np.percentile(seg_dist(P[pm][keep], c - u, c + u)[0], a.pct)) + a.pad
        dall, _ = seg_dist(P, c - 10 * u, c + 10 * u)
        on = dall < rp
        top = c + ((a.cloth_y - c[1]) / u[1]) * u
        by = a.ground_y if a.base_y is None else a.base_y
        bot = c + ((by - c[1]) / u[1]) * u
        caps.append({'a': top.round(4).tolist(), 'b': bot.round(4).tolist(), 'r': round(rp, 4),
                     'points': int(on.sum()), 'length': round(float(np.linalg.norm(bot - top)), 3), 'part': 'pole'})

    # 布：高さ dy の段ごとに 1 本。窓は 2 段ぶん重ねて、まばらな段でも弦が引けるようにする
    ytop = P[:, 1].min()
    first = None
    for y0 in np.arange(ytop, a.cloth_y, a.dy):
        m = (P[:, 1] >= y0 - a.dy / 2) & (P[:, 1] < y0 + 1.5 * a.dy)
        if m.sum() < 25:
            continue
        if first is None:
            first = y0
        Q = P[m][:, [0, 2]]; ww = w[m]
        for _ in range(2):                    # 弦から外れた点を落として引き直す
            cc = np.average(Q, axis=0, weights=ww)
            uu = np.linalg.svd((Q - cc) * np.sqrt(ww)[:, None], full_matrices=False)[2][0]
            nn = np.array([-uu[1], uu[0]])
            perp = (Q - cc) @ nn
            med = np.median(perp); mad = np.median(np.abs(perp - med)) + 1e-3
            ok = np.abs(perp - med) < 4 * mad
            Q, ww = Q[ok], ww[ok]
        cc = np.average(Q, axis=0, weights=ww)
        perp = (Q - cc) @ nn; med = np.median(perp)
        s = (Q - cc) @ uu
        lo, hi = np.percentile(s, [5, 95])
        rr = max(float(np.percentile(np.abs(perp - med), a.pct)) + a.pad, a.min_r)
        # 端の半球が半径ぶん飛び出すので、端を内側に寄せて側面と同じ余白にする
        inset = min((rr - a.pad) / 2, max((hi - lo) / 2 - 0.005, 0))
        lo, hi = lo + inset, hi - inset
        yc = y0 + a.dy / 2
        base = cc + med * nn
        pa = base + lo * uu; pb = base + hi * uu
        A = [pa[0], yc, pa[1]]; B = [pb[0], yc, pb[1]]
        caps.append({'a': np.round(A, 4).tolist(), 'b': np.round(B, 4).tolist(), 'r': round(rr, 4),
                     'points': int(len(Q)), 'length': round(float(hi - lo), 3), 'part': 'cloth'})

    # 先端：最初の密な段より上のまばらな点を 1 本に。PCA では向きが決まらないので端の中央値を結ぶ
    tm = P[:, 1] < first if first is not None else np.zeros(len(P), bool)
    if tm.sum() >= 5:
        Pt = P[tm]; y_top = Pt[:, 1].min(); y_bot = first + a.dy / 2
        hi_ = Pt[Pt[:, 1] < y_top + 0.1]
        lo_ = P[(P[:, 1] >= first) & (P[:, 1] < y_bot)]
        ta = np.array([np.median(hi_[:, 0]), y_top, np.median(hi_[:, 2])])
        tb = np.array([np.median(lo_[:, 0]), y_bot, np.median(lo_[:, 2])])
        d, _ = seg_dist(Pt, ta, tb)
        rt = min(float(np.percentile(d, a.pct)) + a.pad, 0.12)
        caps.append({'a': ta.round(4).tolist(), 'b': tb.round(4).tolist(), 'r': round(rt, 4),
                     'points': int(tm.sum()), 'length': round(float(np.linalg.norm(tb - ta)), 3), 'part': 'tip'})

    for i, c_ in enumerate(caps):
        print(f"#{i:2d} {c_['part']:5s} y {c_['a'][1]:6.2f}  L {c_['length']:4.2f}  r {c_['r']*100:4.1f} cm  pts {c_['points']}")
    json.dump({'version': 1, 'frame': 'ply', 'capsules': caps}, open(a.dst, 'w'), indent=1)
    if a.mesh:
        V, F = capsule_mesh(caps); write_mesh_ply(a.mesh, V, F)
    print(len(caps), 'capsules')


if __name__ == '__main__':
    main()
