#!/usr/bin/env python3
"""ゲートやフラッグの splat を、パーツごとのカプセルに当てる。**膨らませない。**

    python3 tools/fit_parts.py in.ply out.parts.json [--ground-y 0.85] [--mesh out.ply]

密度場から 1 枚のメッシュを焼くと、inflate の下限と格子の粗さで細い部材が太る
（ゲート 24 の柱 7.8 cm が 40 cm 超）。ここでは部材を直線の束とみなして、
splat の中心に RANSAC で直線を 1 本ずつ当てては抜き取り、各直線を 1 本の
カプセルにする。曲がった柱は許容幅を外れたところで自然に何本かに分かれる。

半径は部材の軸から splat 中心までの距離の分位点（--radius-pct）に --pad を足したもの。
どの値で見た目の幅に合うかは、描いた絵の走査線で測って決める（幅は絵で測る。
splat 中心の分布は、見た目の幅そのものではない）。

入力はゲート 1 本だけを切り出した ply（座標はキャプチャの ply のまま、Y-down）。
地面は --ground-y より下（y が大きい側）を捨てるか、--ground-npz の高さ地図で splat ごとに切る。出力の座標も同じフレームで、
鏡映は collision.bin と同じくランタイムが掛ける。
"""
import argparse, json, re
import numpy as np

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


def ground_under(path, P):
    """高さ地図から、各点の真下の地面の y（ply は Y-down）。範囲外は端の値。"""
    G = np.load(path); h = G['h']; c = float(G['cell'])
    j = np.clip(((P[:, 0] - float(G['x0'])) / c).astype(int), 0, h.shape[1] - 1)
    i = np.clip(((P[:, 2] - float(G['z0'])) / c).astype(int), 0, h.shape[0] - 1)
    return h[i, j]


def seg_dist(P, a, b):
    """点群から線分 a-b までの距離と、軸上の位置 t（0..1 が線分内）。"""
    d = b - a; L2 = d @ d
    t = ((P - a) @ d) / L2
    q = a + np.clip(t, 0, 1)[:, None] * d
    return np.linalg.norm(P - q, axis=1), t


def longest_run(s, gap):
    """軸上の座標 s を並べ、gap より空いたところで切った最大の塊の添字。"""
    o = np.argsort(s); ss = s[o]
    cut = np.flatnonzero(np.diff(ss) > gap) + 1
    parts = np.split(o, cut)
    return max(parts, key=len)


def fit(P, w, tol, gap, trials, rng):
    best = None
    n = len(P)
    for _ in range(trials):
        i, j = rng.choice(n, 2, replace=False)
        d = P[j] - P[i]; L = np.linalg.norm(d)
        if L < 0.15:
            continue
        u = d / L
        v = P - P[i]
        s = v @ u
        perp = np.linalg.norm(v - s[:, None] * u, axis=1)
        inl = np.flatnonzero(perp < tol)
        if len(inl) < 50:
            continue
        run = inl[longest_run(s[inl], gap)]
        score = w[run].sum()
        if best is None or score > best[0]:
            best = (score, run)
    if best is None:
        return None
    run = best[1]
    # 重み付き PCA で軸を引き直し、その軸でもう一度だけ集め直す
    for _ in range(2):
        c = np.average(P[run], axis=0, weights=w[run])
        X = (P[run] - c) * np.sqrt(w[run])[:, None]
        u = np.linalg.svd(X, full_matrices=False)[2][0]
        v = P - c; s = v @ u
        perp = np.linalg.norm(v - s[:, None] * u, axis=1)
        inl = np.flatnonzero(perp < tol)
        run = inl[longest_run(s[inl], gap)]
    s_run = (P[run] - c) @ u
    lo, hi = np.percentile(s_run, [1, 99])
    return c + lo * u, c + hi * u, run


def merge_panels(caps, P, w, angle=12.0, dist=0.35, overlap=0.6, pad=0.0, min_thick=0.03, min_width=0.15):
    """平行で近いカプセルの組を 1 つの薄い箱にまとめる。バナーのような板は、RANSAC が
    ぼやけた厚みを層ごとに拾って 4〜5 本の平行なカプセルになり、箱より 10〜20 cm 太る。
    箱の寸法は、組の点を軸に垂直な面で PCA して取る（幅は p2〜p98、厚みは p10〜p90）。"""
    n = len(caps)
    if n < 2:
        return list(caps), []
    A = np.array([c['a'] for c in caps]); B = np.array([c['b'] for c in caps])
    U = B - A; L = np.linalg.norm(U, axis=1); U /= L[:, None]
    parent = list(range(n))
    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]; i = parent[i]
        return i
    cosmin = np.cos(np.radians(angle))
    for i in range(n):
        for j in range(i + 1, n):
            if abs(U[i] @ U[j]) < cosmin:
                continue
            # j の両端を i の軸に落とし、重なりと軸間距離を見る
            u = U[i]; sj = np.array([(A[j] - A[i]) @ u, (B[j] - A[i]) @ u])
            lo, hi = max(0, sj.min()), min(L[i], sj.max())
            if hi - lo < overlap * max(L[i], L[j]):       # 板の層は板の長さを共有する。
                continue                                   # 柱と根元の金具は共有しない
            mid = A[i] + u * (lo + hi) / 2
            v = (A[j] + B[j]) / 2 - mid; v -= (v @ u) * u
            if np.linalg.norm(v) < dist:
                parent[find(i)] = find(j)
    groups = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(i)
    boxes, keep = [], []
    for g in groups.values():
        if len(g) < 2:
            keep.append(caps[g[0]]); continue
        idx = np.concatenate([caps[i]['_idx'] for i in g])
        Q = P[idx]; ww = w[idx]
        u = sum(U[i] * np.sign(U[i] @ U[g[0]]) * L[i] for i in g); u /= np.linalg.norm(u)
        c = np.average(Q, axis=0, weights=ww)
        X = Q - c; sx = X @ u; perp = X - sx[:, None] * u
        ev, evec = np.linalg.eigh(np.cov(perp.T, aweights=ww))
        v1 = evec[:, np.argmax(ev)]; v1 -= (v1 @ u) * u; v1 /= np.linalg.norm(v1)
        v2 = np.cross(u, v1)
        p1 = perp @ v1; p2 = perp @ v2
        lo1, hi1 = np.percentile(p1, [2, 98]); lo2, hi2 = np.percentile(p2, [15, 85])
        if hi1 - lo1 < min_width:                          # 幅の無い組は、筒のぼやけ
            keep += [caps[i] for i in g]; continue
        slo, shi = np.percentile(sx, [1, 99])
        centre = c + u * (slo + shi) / 2 + v1 * (lo1 + hi1) / 2 + v2 * (lo2 + hi2) / 2
        size = [float(shi - slo) + 2 * pad, float(hi1 - lo1) + 2 * pad, max(float(hi2 - lo2), min_thick) + 2 * pad]
        # 軸は u, v, w の 3 本に平らに書く。ランタイムは JsonUtility で読み、入れ子の配列が読めない
        boxes.append({'c': centre.round(4).tolist(), 'u': u.round(5).tolist(), 'v': v1.round(5).tolist(),
                      'w': v2.round(5).tolist(), 'size': [round(x, 4) for x in size],
                      'points': int(len(idx)), 'from': len(g)})
    return keep, boxes


def box_mesh(boxes):
    V, F = [], []
    for b in boxes:
        c = np.array(b['c']); R = np.array([b['u'], b['v'], b['w']]); h = np.array(b['size']) / 2
        base = len(V)
        for sx in (-1, 1):
            for sy in (-1, 1):
                for sz in (-1, 1):
                    V.append(c + R.T @ (np.array([sx, sy, sz]) * h))
        for f in ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)):
            F += [(base + f[0], base + f[1], base + f[2]), (base + f[0], base + f[2], base + f[3])]
    return np.array(V, np.float32).reshape(-1, 3), np.array(F, np.int32).reshape(-1, 3)


def join_ends(caps, reach):
    """各カプセルの端を、近くの別カプセルの軸に届くまで自分の軸に沿って延ばす。
    延ばすのは外向き（端から先へ）だけで、届く点は相手の線分上の最近点。"""
    n = 0
    for i, c in enumerate(caps):
        for end, other_end in (('a', 'b'), ('b', 'a')):
            P = np.array(c[end]); Q = np.array(c[other_end])
            u = (P - Q) / np.linalg.norm(P - Q)
            best = None
            for j, d in enumerate(caps):
                if j == i:
                    continue
                A, B = np.array(d['a']), np.array(d['b'])
                # 自分の軸の延長（P + t u, 0 <= t <= reach）と相手の線分の最近点
                w = B - A; r = P - A
                aa, bb, cc = u @ u, u @ w, w @ w; dd, ee = u @ r, w @ r
                den = aa * cc - bb * bb
                if den < 1e-9:
                    continue
                t = np.clip((bb * ee - cc * dd) / den, 0, reach)
                s_ = np.clip((w @ (P + t * u - A)) / cc, 0, 1)
                gap = np.linalg.norm(P + t * u - (A + s_ * w))
                if gap < c['r'] + d['r'] and t > 0.005 and (best is None or t < best):
                    best = t
            if best is not None:
                c[end] = (P + best * u).round(4).tolist(); n += 1
        c['length'] = round(float(np.linalg.norm(np.subtract(c['b'], c['a']))), 3)
    return n


def capsule_mesh(caps, seg=16, rings=4):
    """カプセルを三角形に。描画と幅の測定に使うだけで、ランタイムはプリミティブを使う。"""
    V, F = [], []
    for c in caps:
        a, b, r = np.array(c['a']), np.array(c['b']), c['r']
        ax = b - a; L = np.linalg.norm(ax); u = ax / L
        t = np.cross(u, [0, 0, 1] if abs(u[2]) < 0.9 else [1, 0, 0]); t /= np.linalg.norm(t)
        s = np.cross(u, t)
        rows = []
        for k in range(rings, -1, -1):             # a 側の半球
            ph = np.pi / 2 * k / rings
            rows.append((a - u * r * np.sin(ph), r * np.cos(ph)))
        for k in range(0, rings + 1):              # b 側の半球
            ph = np.pi / 2 * k / rings
            rows.append((b + u * r * np.sin(ph), r * np.cos(ph)))
        base = len(V)
        ang = np.linspace(0, 2 * np.pi, seg, endpoint=False)
        for cen, rr in rows:
            for g in ang:
                V.append(cen + rr * (np.cos(g) * t + np.sin(g) * s))
        for i in range(len(rows) - 1):
            for j in range(seg):
                p0 = base + i * seg + j; p1 = base + i * seg + (j + 1) % seg
                q0 = p0 + seg; q1 = p1 + seg
                F += [(p0, q0, p1), (p1, q0, q1)]
    return np.array(V, np.float32), np.array(F, np.int32)


def write_mesh_ply(path, V, F):
    with open(path, 'wb') as f:
        f.write(f'ply\nformat binary_little_endian 1.0\nelement vertex {len(V)}\n'
                'property float x\nproperty float y\nproperty float z\n'
                f'element face {len(F)}\nproperty list uchar int vertex_indices\nend_header\n'.encode())
        f.write(V.astype('<f4').tobytes())
        rec = np.zeros(len(F), dtype=[('c', 'u1'), ('i', '<i4', 3)]); rec['c'] = 3; rec['i'] = F
        f.write(rec.tobytes())


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('src'); ap.add_argument('dst')
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument('--ground-y', type=float,
                   help='これより下（y が大きい側、ply は Y-down）を地面として捨てる')
    g.add_argument('--ground-npz',
                   help='地面の高さ地図（h, x0, z0, cell。穴埋め済み）。splat ごとに真下の地面を引き、'
                        'そこから --ground-margin より下を捨てる。地面のコリジョンと同じ地図を渡す')
    ap.add_argument('--ground-margin', type=float, default=0.05)
    ap.add_argument('--snap-npz',
                    help='地面のコリジョンの高さ地図。根元が --snap-dist 以内で止まった縦の部材を、'
                         '軸に沿ってこの地面まで延ばす（脚の下に隙間を残さない）')
    ap.add_argument('--snap-dist', type=float, default=0.35)
    ap.add_argument('--drop-low', type=float, default=0.3,
                    help='--snap-npz の地面から両端ともこの高さ以内の、水平に近いカプセルを捨てる。'
                         '芝の筋に当たった線と、地面に寝た部材。その高さは地面のコリジョンが受け持つ')
    ap.add_argument('--min-opacity', type=float, default=0.25)
    ap.add_argument('--max-scale', type=float, default=0.05,
                    help='中間軸がこれを超える splat は霞として捨てる m。最長軸で切ると、'
                         '一様な色の部材（黒い支柱）に沿って伸びた針まで落ちて部材ごと消える')
    ap.add_argument('--tol', type=float, default=0.035, help='直線からの許容距離 m')
    ap.add_argument('--gap', type=float, default=0.15, help='軸上でこれ以上空いたら別の部材 m')
    ap.add_argument('--min-points', type=int, default=120)
    ap.add_argument('--min-length', type=float, default=0.2)
    ap.add_argument('--radius-pct', type=float, default=75)
    ap.add_argument('--pad', type=float, default=0.0, help='半径に足す m')
    ap.add_argument('--join', type=float, default=0.6,
                    help='カプセルの端が別のカプセルの軸からこの距離以内なら、軸に沿ってそこまで延ばす。'
                         'RANSAC は継ぎ目の手前で切れやすく（端は 1/99 パーセンタイル）、'
                         'ゲートの角に隙間が残るため。0 で無効')
    ap.add_argument('--panels', action='store_true',
                    help='平行で近いカプセルの組を薄い箱にまとめる（バナー状の板）')
    ap.add_argument('--trials', type=int, default=3000)
    ap.add_argument('--seed', type=int, default=0)
    ap.add_argument('--mesh', help='カプセルを三角形にした ply（確認用）')
    a = ap.parse_args()

    r = read_ply(a.src)
    P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
    op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
    smid = np.sort(np.exp(np.stack([r[f'scale_{i}'] for i in range(3)], 1).astype(np.float64)), 1)[:, 1]
    if a.ground_npz:
        gy = ground_under(a.ground_npz, P) - a.ground_margin
    else:
        gy = a.ground_y
    keep = (op >= a.min_opacity) & (smid <= a.max_scale) & (P[:, 1] < gy)
    P, w = P[keep], op[keep]
    print(f'{a.src.split("/")[-1]}: {len(r):,} splats -> {len(P):,} used')

    rng = np.random.default_rng(a.seed)
    alive = np.ones(len(P), bool)
    caps = []
    while alive.sum() > a.min_points:
        idx = np.flatnonzero(alive)
        got = fit(P[idx], w[idx], a.tol, a.gap, a.trials, rng)
        if got is None:
            break
        p0, p1, run = got
        L = np.linalg.norm(p1 - p0)
        if len(run) < a.min_points or L < a.min_length:
            break
        dist, t = seg_dist(P[idx], p0, p1)
        near = (dist < a.tol * 1.5) & (t > -0.05) & (t < 1.05)
        rad = float(np.percentile(dist[run], a.radius_pct)) + a.pad
        caps.append({'a': p0.round(4).tolist(), 'b': p1.round(4).tolist(), 'r': round(rad, 4),
                     'points': int(len(run)), 'length': round(float(L), 3), '_idx': idx[run]})
        alive[idx[near]] = False
        print(f'  #{len(caps):2d}  L {L:5.2f} m  r {rad*100:4.1f} cm  pts {len(run):5d}'
              f'  a {np.round(p0,2)} b {np.round(p1,2)}')
    print(f'{len(caps)} capsules, {alive.sum():,} points left unassigned')
    boxes = []
    if a.panels:
        caps, boxes = merge_panels(caps, P, w, pad=a.pad)
        print(f'{len(boxes)} panels (boxes) merged from parallel capsules, {len(caps)} capsules left')
    for c in caps:
        c.pop('_idx', None)
    if a.snap_npz:
        snapped = 0
        for c in caps:
            A, B = np.array(c['a']), np.array(c['b'])
            lo, hi = (A, B) if A[1] > B[1] else (B, A)     # lo = 下の端（y が大きい）
            u = (lo - hi) / np.linalg.norm(lo - hi)
            g = ground_under(a.snap_npz, lo[None])[0]
            if u[1] > 0.5 and 0 < g - lo[1] < a.snap_dist:
                new = lo + u * (g - lo[1]) / u[1]
                if A[1] > B[1]: c['a'] = new.round(4).tolist()
                else: c['b'] = new.round(4).tolist()
                c['length'] = round(float(np.linalg.norm(np.subtract(c['b'], c['a']))), 3)
                snapped += 1
        for b in boxes:
            u = np.array(b['u']); c = np.array(b['c']); Lb = b['size'][0]
            if u[1] < 0: u = -u                              # u が下向き（+y）
            if u[1] < 0.5: continue
            lo = c + u * Lb / 2
            g = ground_under(a.snap_npz, lo[None])[0]
            if 0 < g - lo[1] < a.snap_dist:
                ext = (g - lo[1]) / u[1]
                b['c'] = (c + u * ext / 2).round(4).tolist(); b['size'][0] = round(Lb + ext, 4); snapped += 1
        print(f'{snapped} members extended down to the ground')
        def low(c):
            A, B = np.array(c['a']), np.array(c['b'])
            g = ground_under(a.snap_npz, np.stack([A, B]))
            u = (B - A) / np.linalg.norm(B - A)
            return abs(u[1]) < 0.5 and ((g - np.array([A[1], B[1]])) < a.drop_low).all()
        n0 = len(caps); caps = [c for c in caps if not low(c)]
        print(f'{n0 - len(caps)} low horizontal capsules dropped (ground covers them), {len(caps)} kept')

    if a.join > 0:
        joined = join_ends(caps, a.join)
        print(f'{joined} capsule ends extended to meet a neighbour')

    json.dump({'version': 1, 'frame': 'ply', 'capsules': caps, 'boxes': boxes}, open(a.dst, 'w'), indent=1)
    if a.mesh:
        V, F = capsule_mesh(caps)
        if boxes:
            Vb, Fb = box_mesh(boxes); F = np.concatenate([F, Fb + len(V)]); V = np.concatenate([V, Vb])
        write_mesh_ply(a.mesh, V, F)


if __name__ == '__main__':
    main()
