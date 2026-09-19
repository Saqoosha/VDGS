"""Top and side views of the COLMAP cameras (coloured by frame time) and sparse points,
next to the GPS track, so a broken reconstruction shows up as a shape mismatch."""
import sys, math, numpy as np, matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
sys.path.insert(0, "tools"); import gps_fit as g
cen, downs = g.read_cameras("sparse/0/images.txt")
names = sorted(cen); C = np.array([cen[n] for n in names])
tt = np.array([int(n.split("_")[1].split(".")[0]) / 59.94 + (310 if n.startswith("b_") else 0) for n in names])
P = []
for l in open("sparse/0/points3D.txt"):
    if l.startswith("#"): continue
    p = l.split(); P.append([float(p[1]), float(p[2]), float(p[3]), int(p[4]), int(p[5]), int(p[6]), float(p[7])])
P = np.array(P); print("points", len(P), "err p50", np.median(P[:, 6]))
# orient the plot with the fitted up: rotate COLMAP into ENU via the GPS fit
import json
m = json.load(open("gps_matrix.json")); Pm = np.array([[1,0,0],[0,0,1],[0,1,0]], float)
R = Pm.T @ np.array(m["R"]); s = m["scale"]; t = Pm.T @ np.array(m["t"])
Ce = s * C @ R.T + t; Pe = s * P[:, :3] @ R.T + t
# GPS track
lat0, lon0 = m["origin"]["lat"], m["origin"]["lon"]; mx = 111320 * math.cos(math.radians(lat0)); my = 110574
G = []
for k in "ab":
    t_, la, lo, al = g.read_gps(f"tools/gps_{k}.csv")
    G.append(np.c_[(lo - lon0) * mx, (la - lat0) * my, al])
G = np.vstack(G)
inb = (np.abs(Pe[:, 0]) < 150) & (np.abs(Pe[:, 1]) < 150) & (np.abs(Pe[:, 2]) < 60)
print(f"points inside 150m box: {inb.sum()} / {len(Pe)}")
fig, ax = plt.subplots(2, 2, figsize=(16, 14))
a = ax[0, 0]; a.scatter(Pe[inb, 0], Pe[inb, 1], s=0.2, c=P[inb, 3:6] / 255, alpha=0.6)
sc = a.scatter(Ce[:, 0], Ce[:, 1], s=6, c=tt, cmap="jet"); a.set_title("top view (ENU via GPS fit): points + cameras by time"); a.set_aspect("equal"); plt.colorbar(sc, ax=a, label="s")
a = ax[0, 1]; a.plot(G[:, 0], G[:, 1], "k-", lw=0.5, label="GPS"); a.scatter(Ce[:, 0], Ce[:, 1], s=6, c=tt, cmap="jet", label="COLMAP cams"); a.set_title("GPS track vs fitted camera centres"); a.set_aspect("equal"); a.legend()
a = ax[1, 0]; a.scatter(Pe[inb, 0], Pe[inb, 2], s=0.2, c=P[inb, 3:6] / 255, alpha=0.6); a.scatter(Ce[:, 0], Ce[:, 2], s=6, c=tt, cmap="jet"); a.set_title("side view (E, up)"); a.set_aspect("equal")
a = ax[1, 1]; a.plot(tt, np.linalg.norm(s * C @ R.T + t - np.array([np.interp(x, np.arange(len(G)), G[:, i]) for i in range(3)]).T if False else Ce - Ce, axis=1)) if False else None
# per-camera residual against GPS at frame time
E = []
clips = {"a": g.read_gps("tools/gps_a.csv"), "b": g.read_gps("tools/gps_b.csv")}
for n in names:
    pre, idx = n.split("_", 1); tf = int(idx.split(".")[0]) / 59.94 + m["offset_s"]
    t_, la, lo, al = clips[pre]; j = int(np.abs(t_ - tf).argmin()); E.append([(lo[j] - lon0) * mx, (la[j] - lat0) * my, al[j]])
E = np.array(E); r = np.linalg.norm(Ce - E, axis=1)
a.plot(tt, r, ".", ms=3); a.set_title("camera vs GPS residual [m] over time"); a.set_xlabel("s"); a.set_ylim(0, 20)
plt.tight_layout(); plt.savefig("out/sfm_overview.png", dpi=80)
print("residual: p50 %.2f p90 %.2f max %.1f" % (np.median(r), np.percentile(r, 90), r.max()))
bad = r > 5; print("cams >5m off:", bad.sum(), [names[i] for i in np.where(bad)[0][:15]])
