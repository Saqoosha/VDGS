#!/usr/bin/env python3
"""地面のコリジョンを、見えている面の高さ地図から 1 枚のメッシュとして作る。

    python3 tools/ground_heightfield.py surface.npz out.ply [--median 3] [--tris 200000]

入力は tools/ground_surface.py の npz（真上から積んだ不透明度が 0.5 を越える高さ）。
密度場の殻と違って太らせも等値面も通らないので、面は見えている地面そのものに乗る。

- スキャンの穴（不透明度が 0.5 に届かないセル）は最寄りの値で埋め、埋めたセルだけならす
- --median（セル数）の中央値で、1 セルだけ飛び出す迷い splat や柱の根元を消す。
  それより大きい起伏は残る
- --tris まで二次誤差で間引く（平らなところから減る）

三角形は ply フレーム（Y-down）で上（-y）を向く。ランタイムは鏡映のとき巻き順も
入れ替えるので、Unity でも上を向く。PhysX の三角形は片面なので、向きを逆にすると
地面の下に落ちる。
"""
import argparse
import numpy as np
from scipy import ndimage


def write_ply(path, V, F):
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
    ap.add_argument('--median', type=int, default=3, help='中央値フィルタの幅（セル）、1 で無効')
    ap.add_argument('--tris', type=int, default=0, help='間引き後の三角形数、0 で間引かない')
    ap.add_argument('--spike', type=float, default=0.3,
                    help='周囲 7 セルの中央値よりこれ以上高いセルを穴にしてから埋める m。0 で無効')
    ap.add_argument('--pit', type=float, default=0.15,
                    help='周囲 1.5 m の p25 よりこれ以上深いセルを穴にしてから埋める m。0 で無効')
    ap.add_argument('--grid', help='穴埋めと中央値の済んだ高さ地図を npz で書く（fit_parts.py --ground-npz 用）')
    a = ap.parse_args()

    G = np.load(a.src)
    h = G['h'].copy(); cell = float(G['cell']); x0, z0 = float(G['x0']), float(G['z0'])
    # 柱の根元は 1〜3 セルの針（2 m 級）として面に残り、隣の穴を最寄り値で埋めると
    # その針の高さで埋まって円錐になる。穴を埋める前に、周囲の中央値より --spike 以上
    # 高い（Y-down なので小さい）セルを穴にする
    if a.spike > 0:
        filled0 = np.where(np.isfinite(h), h, np.nanmedian(h))
        local = ndimage.median_filter(filled0, size=7)
        spike = np.isfinite(h) & (h < local - a.spike)
        h[spike] = np.nan
        print(f'spikes removed: {spike.sum()} cells')
    # 芝の薄い区画では不透明度が 0.5 に届く高さが深くなり、幅 30 cm・深さ 30〜40 cm の穴が
    # 残る（飛行範囲で 20 cm 超が 4%）。ボールはそこへ転がり込む。刈った芝地にそんな穴は
    # 無いので、周囲 1.5 m の 25 パーセンタイル（高い側）より --pit 以上深いセルは穴にする
    if a.pit > 0:
        filled0 = np.where(np.isfinite(h), h, np.nanmedian(h))
        local = ndimage.percentile_filter(filled0, 25, size=15)
        deep = np.isfinite(h) & (h > local + a.pit)
        h[deep] = np.nan
        print(f'pits removed: {deep.sum()} cells')
    hole = ~np.isfinite(h)
    idx = ndimage.distance_transform_edt(hole, return_distances=False, return_indices=True)
    h = h[tuple(idx)]
    if hole.any():
        sm = ndimage.uniform_filter(h, 5)
        h[hole] = sm[hole]
    if a.median > 1:
        h = ndimage.median_filter(h, size=a.median)
    print(f'grid {h.shape[1]}x{h.shape[0]} @ {cell} m, filled {hole.mean()*100:.1f}% of cells')
    if a.grid:
        np.savez_compressed(a.grid, h=h, x0=x0, z0=z0, cell=cell)

    H, W = h.shape
    xs = x0 + (np.arange(W) + 0.5) * cell; zs = z0 + (np.arange(H) + 0.5) * cell
    X, Z = np.meshgrid(xs, zs)
    V = np.stack([X.ravel(), h.ravel(), Z.ravel()], 1)
    i = (np.arange(H - 1)[:, None] * W + np.arange(W - 1)[None, :]).ravel()
    # cross(b-a, c-a) が -y を向く並び（ply は Y-down なので上）
    F = np.concatenate([np.stack([i, i + 1, i + W], 1), np.stack([i + 1, i + W + 1, i + W], 1)])
    n = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]])
    assert (n[:, 1] < 0).all(), 'winding must face -y (up in the ply frame)'
    if a.tris and a.tris < len(F):
        import fast_simplification
        V, F = fast_simplification.simplify(V.astype(np.float32), F.astype(np.int32),
                                            target_reduction=1 - a.tris / len(F))
    print(f'{len(F):,} triangles')
    write_ply(a.dst, np.asarray(V), np.asarray(F))


if __name__ == '__main__':
    main()
