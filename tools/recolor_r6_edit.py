#!/usr/bin/env python3
"""Apply JDL-2026-R6's SuperSplat colour edit to another capture of the same place.

The constants below are not a general grade. They are the edit Saqoosha made in
SuperSplat on JDL-2026-R6-fix-unity -> -fix-unity-edit, recovered per splat: the two
files hold the same 2,987,466 splats in the same order and differ only in f_dc, so a
least-squares fit in base colour (0.5 + C0*f_dc) returns the edit itself. It came out
as exactly saturation about Rec.601 luma, then gain, then offset - SuperSplat's own
sliders - with residual rms 0.005-0.014 over the visible range.

SH (f_rest_*) is left alone, matching what the SuperSplat edit itself did.

    python3 tools/recolor_r6_edit.py in.ply out.ply
"""
import sys, numpy as np
C0 = 0.2820948
S, W, G, T = 1.43025, np.array([0.30004, 0.58839, 0.11096]), 1.69105, -0.13892

src, dst = sys.argv[1], sys.argv[2]
f = open(src, 'rb'); h = b''
while not h.endswith(b'end_header\n'): h += f.read(1)
hdr = h.decode(); props = [l.split()[2] for l in hdr.splitlines() if l.startswith('property')]
n = int([l for l in hdr.splitlines() if l.startswith('element vertex')][0].split()[2])
d = np.fromfile(f, dtype=np.float32, count=n * len(props)).reshape(n, len(props))
c = {k: i for i, k in enumerate(props)}
dc = [c['f_dc_0'], c['f_dc_1'], c['f_dc_2']]

X = 0.5 + C0 * d[:, dc].astype(np.float64)
Y = G * (S * X + (1 - S) * (X @ W)[:, None]) + T
d[:, dc] = ((Y - 0.5) / C0).astype(np.float32)

with open(dst, 'wb') as g:
    g.write(hdr.encode()); d.tofile(g)
print(f'{dst}: {n} splats  colour {np.round(X.mean(0),3)} -> {np.round(Y.mean(0),3)}')
