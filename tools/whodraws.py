#!/usr/bin/env python3
"""地面すれすれの視点から、**どの形の splat が画面を覆っているか**を測る。

**σ_y（縦の突き出し）で追ったのは外れだった。** 潰しても見た目が変わらなかったので、
覆っているのは別の集団。ここでは形で分けたうえで、**低い視点からの被覆寄与**を
クラスごとに出す。印象で「これが原因」と言わない。

視点はドローンが実際に飛ぶ高さ（既定 0.3 / 0.6 / 1.0 m）。**3m では見えない。**
被覆寄与は (最長軸 * 焦点距離 / 距離)^2 * 不透明度。

    python3 whodraws.py a.ply [b.ply ...]
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('plys', nargs='+')
ap.add_argument('--h', type=float, nargs='+', default=[0.3, 0.6, 1.0])
ap.add_argument('--n', type=int, default=36)
ap.add_argument('--band', type=float, default=3.0)
ap.add_argument('--cell', type=float, default=2.0)
ap.add_argument('--fov', type=float, default=120.0)
ap.add_argument('--res', type=int, default=1024)
a = ap.parse_args()

for path in a.plys:
    f = open(path, 'rb'); head = b''
    while b'end_header' not in head:
        head += f.read(1 << 16)
    end = head.index(b'end_header') + len(b'end_header') + 1
    txt = head[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', txt).group(1))
    dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
    r = np.array(np.memmap(path, dtype=dt, mode='r', offset=end, shape=(n,)))

    P = np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)
    op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
    S = np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1).astype(np.float64))
    Q = np.stack([r['rot_0'], r['rot_1'], r['rot_2'], r['rot_3']], 1).astype(np.float64)
    Q /= np.maximum(np.linalg.norm(Q, axis=1, keepdims=True), 1e-12)
    live = op > 0.1
    smax = S.max(1)
    w, x, y, z = Q[:, 0], Q[:, 1], Q[:, 2], Q[:, 3]
    R = np.empty((n, 3, 3))
    R[:, 0, 0] = 1-2*(y*y+z*z); R[:, 0, 1] = 2*(x*y-w*z); R[:, 0, 2] = 2*(x*z+w*y)
    R[:, 1, 0] = 2*(x*y+w*z); R[:, 1, 1] = 1-2*(x*x+z*z); R[:, 1, 2] = 2*(y*z-w*x)
    R[:, 2, 0] = 2*(x*z-w*y); R[:, 2, 1] = 2*(y*z+w*x); R[:, 2, 2] = 1-2*(x*x+y*y)
    sig = np.sqrt(((R * S[:, None, :]) ** 2).sum(2))
    sy = sig[:, 1]

    ordS = np.argsort(S, 1)
    smin = np.take_along_axis(S, ordS[:, :1], 1)[:, 0]
    smid = np.take_along_axis(S, ordS[:, 1:2], 1)[:, 0]
    axmin = np.take_along_axis(R, ordS[:, None, :1], 2)[:, :, 0]
    axmax = np.take_along_axis(R, ordS[:, None, 2:], 2)[:, :, 0]
    vmin, vmax_ = np.abs(axmin[:, 1]), np.abs(axmax[:, 1])
    needle = smid / np.maximum(smax, 1e-9) < 0.5
    blob = (~needle) & (smin / np.maximum(smax, 1e-9) > 0.5)
    plate = ~needle & ~blob

    # 局所地面
    vote = live & (smax < 0.15) & (op > 0.5)
    lo = P[vote][:, [0, 2]].min(0); hi = P[vote][:, [0, 2]].max(0)
    nx = int(np.ceil((hi[0]-lo[0])/a.cell))+1; nz = int(np.ceil((hi[1]-lo[1])/a.cell))+1
    ix = np.clip(((P[:, 0]-lo[0])/a.cell).astype(int), 0, nx-1)
    iz = np.clip(((P[:, 2]-lo[1])/a.cell).astype(int), 0, nz-1)
    cid = ix*nz + iz
    gy = np.full(nx*nz, np.nan)
    vc, vy = cid[vote], P[vote, 1]
    o = np.argsort(vc, kind='stable'); vc, vy = vc[o], vy[o]
    bnd = np.searchsorted(vc, np.arange(nx*nz+1))
    for c in range(nx*nz):
        s, e = bnd[c], bnd[c+1]
        if e-s >= 8:
            gy[c] = np.percentile(vy[s:e], 10)
    G = gy.reshape(nx, nz)
    for _ in range(40):
        m = np.isnan(G)
        if not m.any():
            break
        acc = np.zeros_like(G); cnt = np.zeros_like(G)
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            Sft = np.full_like(G, np.nan)
            Sft[max(dx, 0):nx+min(dx, 0), max(dz, 0):nz+min(dz, 0)] = \
                G[max(-dx, 0):nx+min(-dx, 0), max(-dz, 0):nz+min(-dz, 0)]
            ok = ~np.isnan(Sft); acc[ok] += Sft[ok]; cnt[ok] += 1
        fl = m & (cnt > 0); G[fl] = acc[fl]/cnt[fl]
    G = np.nan_to_num(G, nan=0.0)
    h = P[:, 1] - G.reshape(-1)[cid]

    foc = a.res/(2*np.tan(np.radians(a.fov)/2))
    g = int(np.sqrt(a.n))
    cams = np.array([[xx, G.reshape(-1)[np.clip(((xx-lo[0])/a.cell).astype(int), 0, nx-1)*nz
                                       + np.clip(((zz-lo[1])/a.cell).astype(int), 0, nz-1)] + hh, zz]
                     for hh in a.h
                     for xx in np.linspace(*np.percentile(P[live][:, 0], [15, 85]), g)
                     for zz in np.linspace(*np.percentile(P[live][:, 2], [15, 85]), g)])
    cov = np.zeros(n)
    for i in range(0, n, 200_000):
        blk = P[i:i+200_000]
        D = np.linalg.norm(blk[:, None, :]-cams[None, :, :], axis=2)
        np.maximum(D, 0.05, out=D)
        cov[i:i+200_000] = ((smax[i:i+200_000, None]*foc/D)**2).mean(1)*op[i:i+200_000]
    tot = cov[live].sum()

    band = live & (h > -0.5) & (h < a.band)
    print(f'\n=== {path.split("/")[-1]}   生存 {live.sum():,}  '
          f'視点 {len(cams)} 箇所（高さ {a.h} m）')
    print(f'  帯内 σ_y  p99 {np.percentile(sy[band],99):.4f}  最大 {sy[band].max():.4f}'
          f'   帯内 最長軸 p99 {np.percentile(smax[band],99):.3f} 最大 {smax[band].max():.3f}')
    cls = {'お椀': blob, '地面板(法線↑)': plate & (vmin > 0.7), '立った板': plate & (vmin <= 0.7),
           '縦の針': needle & (vmax_ > 0.5), '寝た針': needle & (vmax_ <= 0.5)}
    print(f'  {"形":16s} {"個数":>10s} {"被覆寄与":>9s} {"最長軸p50":>10s} {"p99":>7s} {"σ_y p50":>9s}')
    for nm, m in cls.items():
        k = band & m
        if k.sum() == 0:
            continue
        print(f'  {nm:16s} {k.sum():10,} {cov[k].sum()/tot*100:8.2f}% '
              f'{np.median(smax[k]):10.3f} {np.percentile(smax[k],99):7.3f} '
              f'{np.median(sy[k]):9.4f}')
    print(f'  {"帯の外/死んでいる":16s} {int((live&~band).sum()):10,} '
          f'{cov[live&~band].sum()/tot*100:8.2f}%')
    print(f'  被覆の上位 20,000 個: {np.sort(cov[live])[::-1][:20000].sum()/tot*100:.1f}%'
          f'   最長軸 p50 {np.median(smax[np.argsort(-cov*live)[:20000]]):.3f} m')
