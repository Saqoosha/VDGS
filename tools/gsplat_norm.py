"""Undo the AirVis trainer's normalisation so a `conservative` (frame=normalized) export
can take the GPS matrix like a COLMAP-frame one.

The trainer normalises exactly like gsplat's Parser(normalize=True): similarity_from_cameras
then align_principal_axes. Both are imported from a gsplat checkout (GSPLAT env var or
~/gsplat), reproduced from the COLMAP sparse/0 next to the ply, and checked two ways -
the scale against the trainer's log (0.2619 / camera extent x1.1), and the rotation by
landing the sparse points on the splats, because the PCA step leaves each axis sign to
the eigensolver and the trainer's solver chose differently (JDL-2026-R6: 180 degrees off,
1.3 m median error; the right sign gives 4 cm).

    python3 tools/gsplat_norm.py normalised.ply train1_matrix.json   # run in the dataset dir
"""
import sys, json, numpy as np
from plyfile import PlyData
from scipy.spatial import cKDTree
sys.path.insert(0, "tools"); from gps_fit import qvec2R
import os
sys.path.insert(0, os.path.join(os.environ.get("GSPLAT", os.path.expanduser("~/gsplat")), "examples/datasets")); import normalize as N
ply, out = sys.argv[1], sys.argv[2]
c2w = []
lines = [l for l in open("sparse/0/images.txt") if not l.startswith("#")]
for i in range(0, len(lines), 2):
    p = lines[i].split(); R = qvec2R([float(x) for x in p[1:5]]); t = np.array([float(x) for x in p[5:8]])
    M = np.eye(4); M[:3, :3] = R.T; M[:3, 3] = -R.T @ t; c2w.append(M)
c2w = np.array(c2w)
X = np.array([[float(x) for x in l.split()[1:4]] for l in open("sparse/0/points3D.txt") if not l.startswith("#")])
c2w_n, Xn0, T0 = N.normalize(c2w, X)
s = np.linalg.norm(T0[:3, 0])
cams = c2w_n[:, :3, 3]; ext = np.max(np.linalg.norm(cams - cams.mean(0), axis=1)) * 1.1
print(f"gsplat normalize: scale {s:.5f} (trainer said 0.2619)  camera extent x1.1 = {ext:.5f} (trainer said 1.80336)")
print("  (those two only verify the scale: the extent is rigid-invariant. The rotation is checked below.)")
v = PlyData.read(ply)["vertex"].data
S = np.c_[v["x"], v["y"], v["z"]].astype(np.float64); op = 1 / (1 + np.exp(-v["opacity"].astype(np.float64)))
tree = cKDTree(S[op > 0.1])
# align_principal_axes leaves each axis sign to the eigensolver; the trainer's solver chose
# differently, so try every proper sign flip and keep the one that lands the sparse points
# on the splats. A wrong one is metres off; the right one is centimetres.
best = None
for f in [(1,1,1),(1,-1,-1),(-1,1,-1),(-1,-1,1)]:
    D = np.diag(f).astype(float); T = np.eye(4); T[:3, :3] = D; T = T @ T0
    d, _ = tree.query(N.transform_points(T, X), workers=8)
    m = np.median(d) * 10.83911 / s
    print(f"  flip {f}: sparse -> splat median {m:.3f} m  p90 {np.percentile(d,90)*10.83911/s:.3f} m")
    if best is None or m < best[0]: best = (m, T)
m, T = best
if m > 0.3: sys.exit(f"no flip lands within 0.3 m (best {m:.2f} m): this ply is not in gsplat's normalised frame")
R = T[:3, :3] / s; t = T[:3, 3]
# inverse: normalised -> COLMAP, then -> Unity via gps matrix
Rinv = R.T; sinv = 1 / s; tinv = -Rinv @ t / s
g = json.load(open("gps_matrix.json")); sg, Rg, tg = g["scale"], np.array(g["R"]), np.array(g["t"])
json.dump({"scale": float(sg * sinv), "R": (Rg @ Rinv).tolist(), "t": (sg * Rg @ tinv + tg).tolist(),
           "note": "gsplat-normalised (train1) -> Unity metres"}, open(out, "w"), indent=2)
print("wrote", out, "unit", round(sg * sinv, 3), "m")
