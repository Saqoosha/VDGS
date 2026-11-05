#!/usr/bin/env python3
"""トラックのゲートと旗を全部、カプセルと箱に当てる（屋外シーン用）。

    # 1. 地面から立っている物を列挙して、どれがゲートの骨組みを持つか人が決める
    python3 tools/fit_scene_parts.py scene.ply --track x.track.json --placement placement.json \
        --ground build/.../ground-grid.npz --grass build/.../grass-top-grid.npz --out out/ --list
    # 2. 当てる
    python3 tools/fit_scene_parts.py ... --out out/ --gates 9,11,15 --no-flag 15,21

手順は 3 段：
  1. 地面から 0.5〜8 m に浮いている濃い splat を 20 cm 格子で塊にし、トラックのゲート位置
     （placement を外して ply 座標へ）に最寄りの塊を当てる。木や建物は当たらないので外れる
  2. 旗の布を切り分ける。地面から高い種（ゲートなら 2.4 m 超、旗だけなら 1.2 m 超）から
     下へ 10 cm の段を歩き、真上から見た弦が 15〜85 cm で中心が飛ばない段の最長の連なりを
     布とする（2 段までの切れ目は許す。先端と裾は細くてまばら）。布は fit_flag.py へ
  3. 残り（骨組みとポール）を fit_parts.py へ。芝の上端で切り、板は箱に、脚は地面まで延ばし、
     継ぎ目をつなぐ

ゲートか旗だけかは --list の絵を見て人が決める（--gates）。足元の大きさでは分けられない
（R6b の旗 17 は 1.4 × 1.2 m、ゲート 11 は 1.3 × 1.0 m）。--no-flag は旗が無いと分かって
いる物体（梯子ゲート、5 本柱のゲート）で、高い部材を布と取り違えないため。

出力は out/<name>.parts.json（全部まとめ、ply 座標）と、物体ごとの切り出し・結果・確認画像の
元になる ply。出荷形式にするのは collision_pack.py。
"""
import argparse, json, os, re, subprocess, sys
import numpy as np
from scipy import ndimage
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fit_parts import ground_under, capsule_mesh, box_mesh, write_mesh_ply, join_ends

TOOLS = os.path.dirname(os.path.abspath(__file__))
SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4', 'uint': '<u4'}


def read_ply_rows(path):
    f = open(path, 'rb'); hd = b''
    while b'end_header' not in hd:
        hd += f.read(65536)
    end = hd.index(b'end_header') + 11; t = hd[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', t).group(1))
    dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', t)])
    return t, np.memmap(path, dtype=dt, mode='r', offset=end, shape=(n,))


def save_rows(path, header, rows):
    with open(path, 'wb') as g:
        g.write(re.sub(r'element vertex \d+', f'element vertex {len(rows)}', header).encode())
        g.write(np.ascontiguousarray(rows).tobytes())


def run(args):
    p = subprocess.run([sys.executable] + args, capture_output=True, text=True)
    if p.returncode:
        raise SystemExit(p.stderr)
    return p.stdout


def track_gates_in_ply(track_path, placement_path):
    """トラックのゲート位置（cm、ゲーム座標）を ply 座標へ。placement の position を引き、
    mirrorY（変換済みは true 相当）なら Y を反転する。回転は未対応（R6b は無回転）。"""
    d = json.load(open(track_path)); v = d['value']; v = json.loads(v) if isinstance(v, str) else v
    pl = json.load(open(placement_path))
    assert not any(pl.get('rotation') or [0, 0, 0]), 'rotated placements are not handled'
    px, py, pz = pl['position']; mirror = pl.get('mirrorY') is not False
    out = []
    for g in v['gates']:
        x, y, z = np.array(g['trans']['pos']) / 100.0
        out.append(np.array([x - px, -(y - py) if mirror else y - py, z - pz]))
    return out


def detect_objects(P, op, smid, hag, x0, z0, x1, z1):
    """地面から 0.5〜8 m の濃い小さな splat を 20 cm 格子で塊にする。地面の高さ地図の
    範囲（飛行範囲）の外は見ない。森や建物はそこにあり、当てる対象でもない。"""
    k = (op > 0.3) & (smid < 0.05) & (hag > 0.5) & (hag < 8)
    k &= (P[:, 0] >= x0) & (P[:, 0] < x1) & (P[:, 2] >= z0) & (P[:, 2] < z1)
    c = 0.2
    gi = ((P[k, 2] - z0) / c).astype(int); gj = ((P[k, 0] - x0) / c).astype(int)
    occ = np.zeros((gi.max() + 1, gj.max() + 1)); np.add.at(occ, (gi, gj), 1)
    lab, n = ndimage.label(ndimage.binary_dilation(occ >= 8, iterations=2))
    out = []
    for L in range(1, n + 1):
        m = lab == L
        if occ[m].sum() < 150:
            continue
        zz, xx = np.nonzero(m)
        inb = np.isin(gi * occ.shape[1] + gj, zz * occ.shape[1] + xx)
        Q = P[k][inb]; hq = hag[k][inb]
        out.append(dict(id=len(out), cx=float(np.median(Q[:, 0])), cz=float(np.median(Q[:, 2])),
                        x=[float(Q[:, 0].min()), float(Q[:, 0].max())], z=[float(Q[:, 2].min()), float(Q[:, 2].max())],
                        top=float(hq.max()), n=int(occ[m].sum())))
    return out


def cloth_regions(P, op, smid, hag, seed_h):
    """布の領域（点のマスク）と、その下端の高さ。"""
    seed = (hag > seed_h) & (op > 0.25) & (smid < 0.05)
    if seed.sum() < 30:
        return []
    x0, z0 = P[:, 0].min(), P[:, 2].min()
    gi = ((P[seed, 2] - z0) / 0.1).astype(int); gj = ((P[seed, 0] - x0) / 0.1).astype(int)
    occ = np.zeros((gi.max() + 1, gj.max() + 1)); np.add.at(occ, (gi, gj), 1)
    lab, n = ndimage.label(ndimage.binary_dilation(occ > 0, iterations=3))
    sl = lab[gi, gj]
    found = []
    for L in range(1, n + 1):
        sp = P[seed][sl == L]
        if len(sp) < 30:
            continue
        cen = np.median(sp[:, [0, 2]], 0); R0 = np.percentile(np.linalg.norm(sp[:, [0, 2]] - cen, axis=1), 95) + 0.3
        reg = np.linalg.norm(P[:, [0, 2]] - cen, axis=1) < R0
        q = reg & (op > 0.25) & (smid < 0.05) & (hag > 0.35); ok = []; prev = None
        hs = np.arange(hag[q].max(), 0.35, -0.1)
        for hh in hs:
            s_ = q & (hag <= hh) & (hag > hh - 0.1)
            if s_.sum() < 8:
                ok.append(False); continue
            X = P[s_][:, [0, 2]]; cx_ = np.median(X, 0); X = X - X.mean(0)
            u = np.linalg.svd(X, full_matrices=False)[2][0]; pr = X @ u
            wide = 0.15 <= np.percentile(pr, 95) - np.percentile(pr, 5) < 0.85
            jump = prev is not None and np.linalg.norm(cx_ - prev) > 0.3
            ok.append(bool(wide and not jump))
            if wide: prev = cx_
        best, cur, gap = [], [], 0
        for k_, o_ in enumerate(ok):
            if o_:
                cur.append(k_); gap = 0
            else:
                gap += 1
                if gap > 2:
                    if len(cur) > len(best): best = cur
                    cur, gap = [], 0
        if len(cur) > len(best): best = cur
        if len(best) < 3:
            continue                                  # 裸のポール：骨組みとして扱う
        bottom = hs[best[-1]] - 0.1
        cloth = reg & (hag > bottom)
        if (cloth & (op >= 0.25) & (smid <= 0.05)).sum() < 60:
            continue
        found.append(cloth)
    return found


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('ply')
    ap.add_argument('--track', required=True); ap.add_argument('--placement', required=True)
    ap.add_argument('--ground', required=True, help='ground_heightfield.py --grid の npz（0.5 の面）')
    ap.add_argument('--grass', required=True, help='同じく、芝の上端（0.3 の面）')
    ap.add_argument('--out', required=True)
    ap.add_argument('--list', action='store_true', help='物体を列挙して切り出すだけ')
    ap.add_argument('--gates', default='', help='ゲートの骨組みを持つ物体の id（カンマ区切り）')
    ap.add_argument('--no-flag', default='', help='旗を探さない物体の id')
    ap.add_argument('--pad', type=float, default=0.025)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    gates = {int(x) for x in a.gates.split(',') if x}; noflag = {int(x) for x in a.no_flag.split(',') if x}

    header, rows = read_ply_rows(a.ply)
    P = np.stack([rows['x'], rows['y'], rows['z']], 1).astype(np.float64)
    op = 1 / (1 + np.exp(-rows['opacity'].astype(np.float64)))
    smid = np.sort(np.exp(np.stack([rows[f'scale_{k}'] for k in range(3)], 1).astype(np.float64)), 1)[:, 1]
    G = np.load(a.ground); x0, z0 = float(G['x0']), float(G['z0']); cell = float(G['cell'])
    x1, z1 = x0 + G['h'].shape[1] * cell, z0 + G['h'].shape[0] * cell
    hag = ground_under(a.ground, P) - P[:, 1]

    objs = detect_objects(P, op, smid, hag, x0, z0, x1, z1)
    C = np.array([[o['cx'], o['cz']] for o in objs])
    hit = {}
    for gi_, gp in enumerate(track_gates_in_ply(a.track, a.placement)):
        d = np.linalg.norm(C - gp[[0, 2]], axis=1); j = int(d.argmin())
        if d[j] < 3.0:
            hit.setdefault(j, []).append(gi_)
    print(f'{len(objs)} standing objects, {len(hit)} matched to track gates')
    for j in sorted(hit):
        o = objs[j]
        print(f'  obj {j:2d}  centre ({o["cx"]:6.2f}, {o["cz"]:6.2f})  footprint {o["x"][1]-o["x"][0]:.1f} x {o["z"][1]-o["z"][0]:.1f} m'
              f'  top {o["top"]:.1f} m  track gates {hit[j]}')
    json.dump(objs, open(os.path.join(a.out, 'objects.json'), 'w'), indent=1)

    allcaps, allboxes = [], []
    for j in sorted(hit):
        o = objs[j]; m = 0.5
        gb = ground_under(a.ground, np.array([[o['cx'], 0, o['cz']]]))[0]
        lo = np.array([o['x'][0] - m, gb - o['top'] - 1.0, o['z'][0] - m]); hi = np.array([o['x'][1] + m, gb + 0.5, o['z'][1] + m])
        sel = np.all((P >= lo) & (P <= hi), 1)
        crop = os.path.join(a.out, f'obj{j}-splats.ply'); save_rows(crop, header, rows[sel])
        if a.list:
            continue
        Pc, opc, smc, hc = P[sel], op[sel], smid[sel], hag[sel]
        caps, flagmask, nflags = [], np.zeros(sel.sum(), bool), 0
        if j not in noflag:
            for cloth in cloth_regions(Pc, opc, smc, hc, 2.4 if j in gates else 1.2):
                nflags += 1; flagmask |= cloth
                fp = os.path.join(a.out, f'obj{j}-flag{nflags}.ply'); save_rows(fp, header, rows[sel][cloth])
                fj = os.path.join(a.out, f'obj{j}-flag{nflags}.json')
                ymax = float(Pc[cloth, 1].max())
                run([os.path.join(TOOLS, 'fit_flag.py'), fp, fj, '--ground-y', str(ymax + 0.05),
                     '--cloth-y', str(ymax + 0.01), '--no-pole', '--pad', str(a.pad)])
                for cc in json.load(open(fj))['capsules']:
                    cc['obj'] = j; cc['kind'] = 'flag'; caps.append(cc)
        gp = os.path.join(a.out, f'obj{j}-gate.ply'); save_rows(gp, header, rows[sel][~flagmask])
        gj = os.path.join(a.out, f'obj{j}-gate.json')
        out = run([os.path.join(TOOLS, 'fit_parts.py'), gp, gj, '--ground-npz', a.grass, '--snap-npz', a.ground,
                   '--pad', str(a.pad), '--min-points', '60', '--min-length', '0.15', '--panels'])
        gd = json.load(open(gj)); gc = gd['capsules']; gbx = gd.get('boxes', [])
        for cc in gc: cc['obj'] = j; cc['kind'] = 'gate'
        for bb in gbx: bb['obj'] = j; bb['kind'] = 'gate'
        caps += gc
        if caps:
            join_ends(caps, 0.6)
        json.dump({'version': 1, 'frame': 'ply', 'capsules': caps, 'boxes': gbx},
                  open(os.path.join(a.out, f'obj{j}.parts.json'), 'w'), indent=1)
        allcaps += caps; allboxes += gbx
        print(f'obj {j:2d}: {nflags} flag(s) {sum(1 for c in caps if c["kind"] == "flag")} caps, '
              f'structure {len(gc)} caps {len(gbx)} boxes  | ' + out.strip().splitlines()[-1])
    if a.list:
        return
    name = os.path.splitext(os.path.basename(a.ply))[0]
    json.dump({'version': 1, 'frame': 'ply', 'capsules': allcaps, 'boxes': allboxes},
              open(os.path.join(a.out, f'{name}.parts.json'), 'w'), indent=1)
    V, F = capsule_mesh(allcaps)
    if allboxes:
        Vb, Fb = box_mesh(allboxes); F = np.concatenate([F, Fb + len(V)]); V = np.concatenate([V, Vb])
    write_mesh_ply(os.path.join(a.out, f'{name}.parts.ply'), V, F)
    print(f'{len(allcaps)} capsules, {len(allboxes)} boxes -> {name}.parts.json')


if __name__ == '__main__':
    main()
