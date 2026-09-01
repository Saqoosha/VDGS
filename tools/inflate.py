#!/usr/bin/env python3
"""コリジョンを焼く前に splat を太らせる。**見た目用ではない。**

密度場は格子点でしか評価されない。3DGS の地面は σ_y が 2cm しかない薄いシートなので、
格子間隔 0.12m に対して 6 分の 1 の薄さになり、**格子点が Y 方向で 0.05m 以上外れた柱は
密度ゼロ**になる。格子点とシートの距離は最大 0.06m なので約 17% の柱が外れ、地面が
滑らかなぶん**外れる柱がまとまって大穴になる**。

細かくすれば直るが、2cm を捉えるには res 0.03 が要って 120 億セル。無理。

だから逆に太らせる。最小スケールを格子 1 セル分まで底上げすれば、評価範囲が
sigmas 倍に広がって柱が途切れない。**密度の頂点は alpha のままで変わらない**
（alpha*exp(-0.5 d^T Σ^-1 d) は中心で alpha）ので、iso のしきい値はそのまま使える。

**太いほうが物理的にも正しい。** 物理は 400Hz、150km/h で 1 ステップ 0.104m 進むので、
厚さ 10cm 未満の壁はすり抜ける。

    python3 inflate.py in.ply out.ply --min-scale 0.12
"""
import argparse, re
import numpy as np

SZ = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4'}

ap = argparse.ArgumentParser(description=__doc__,
                             formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument('src'); ap.add_argument('dst')
ap.add_argument('--min-scale', type=float, default=0.12, help='全軸の下限 m')
ap.add_argument('--min-opacity', type=float, default=None,
                help='これ未満の splat を落とす（薄いものは密度に効かないため）')
a = ap.parse_args()

f = open(a.src, 'rb'); head = b''
while b'end_header' not in head:
    head += f.read(1 << 16)
end = head.index(b'end_header') + len(b'end_header') + 1
txt = head[:end].decode('ascii', 'replace')
n = int(re.search(r'element vertex (\d+)', txt).group(1))
dt = np.dtype([(nm, SZ[k]) for k, nm in re.findall(r'property (\w+) (\w+)', txt)])
r = np.array(np.memmap(a.src, dtype=dt, mode='r', offset=end, shape=(n,)))

S = np.exp(np.stack([r['scale_0'], r['scale_1'], r['scale_2']], 1).astype(np.float64))
op = 1 / (1 + np.exp(-r['opacity'].astype(np.float64)))
smin = S.min(1)
print(f'{a.src.split("/")[-1]}  {n:,} splats')
print(f'  最小軸 (m)  p50 {np.percentile(smin,50):.4f}  p90 {np.percentile(smin,90):.4f}'
      f'  p99 {np.percentile(smin,99):.4f}')
print(f'  下限 {a.min_scale} 未満の splat  {int((smin < a.min_scale).sum()):,}'
      f' ({(smin < a.min_scale).mean()*100:.2f}%)')

keep = np.ones(n, bool)
if a.min_opacity is not None:
    keep = op >= a.min_opacity
    print(f'  不透明度 {a.min_opacity} 未満を落とす: {int((~keep).sum()):,}')

Snew = np.maximum(S, a.min_scale)
out = r[keep].copy()
for i in range(3):
    out[f'scale_{i}'] = np.log(Snew[keep, i]).astype(np.float32)
print(f'  太らせた後の最小軸  p50 {np.percentile(Snew.min(1)[keep],50):.4f}'
      f'  最小 {Snew.min(1)[keep].min():.4f}')
hdr = re.sub(r'element vertex \d+', f'element vertex {len(out)}',
             txt[:txt.index('end_header')])
with open(a.dst, 'wb') as g:
    g.write(hdr.encode('ascii')); g.write(b'end_header\n'); g.write(out.tobytes())
print(f'wrote {a.dst}  {len(out):,} splats')
