"""Drop strays (far outside the scene) and giant splats so a viewer can frame the scene."""
import sys, numpy as np
from plyfile import PlyData, PlyElement
src, dst = sys.argv[1], sys.argv[2]
box = float(sys.argv[3]) if len(sys.argv) > 3 else 5.0      # x p90 radius
maxlen = float(sys.argv[4]) if len(sys.argv) > 4 else None  # absolute, in file units
p = PlyData.read(src); v = p["vertex"].data
xyz = np.c_[v["x"], v["y"], v["z"]].astype(np.float64)
sc = np.exp(np.c_[v["scale_0"], v["scale_1"], v["scale_2"]].astype(np.float64)).max(1)
med = np.median(xyz, 0); r = np.linalg.norm(xyz - med, axis=1); r90 = np.percentile(r, 90)
keep = r < box * r90
if maxlen is None: maxlen = np.percentile(sc, 99.9)
keep &= sc < maxlen
print(f"keep {keep.sum():,}/{len(v):,}: strays {int((r >= box*r90).sum()):,}, giants(>{maxlen:.3f}) {int((sc >= maxlen).sum()):,}")
PlyData([PlyElement.describe(v[keep], "vertex")], text=False).write(dst)
