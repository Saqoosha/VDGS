"""Bundle adjustment of every DVR frame's pose against the fixed 3DGS map: the per-frame CPR correspondences
(DVR pixel <-> map point, saved by cpr_match.py DUMP=) as reprojection residuals, plus a motion prior on linear
and angular acceleration and on position jerk between neighbouring frames. One Gauss-Newton solve over all frames at once, so a frame
whose own points leave its position loose (fast turns: blur removes the near field) is pinned by what its
neighbours measured, not by a smoother. Frames whose own points disagree with the solved track (Mahalanobis in
their own information) are dropped and the track solved again.
usage: cpr_ba.py init_poses.json match.jsonl:dump_dir [match.jsonl:dump_dir ...] out_poses.json
  later pairs replace earlier ones frame by frame (round 3 over round 2)
Position weight (1 - k)^2 per frame: k = how far cpr_track.py's answer moved with a 2 m shift of the render viewpoint
(1 = the position only echoes where it was rendered from: far trees only, #2437-#2476).
env: SIG_PX (4), SIG_ACC m/s^2 (40), SIG_ALPHA rad/s^2 (50), SIG_JERK m/s^3 (300, 0 = off), MAX_PTS per frame (300, in equal quotas per distance band), FAR_OUT m (15),
GATE Mahalanobis^2 (1000), TAKEOFF, CAM (camera json)
Motion prior, measured on FDF R6b d05 (60 fps, same tracker matches each time; jitter = distance from a 0.25 s local fit):
  acc 40 / alpha 100 / gate 250 (the first defaults): jitter p50 1.45 cm p90 4.3, 4518 frames on their own points.
    The view visibly shakes when a gate is 1-2 m away (e02 #1346-#1479: 2.2 / 5.4 cm).
  acc 15: jitter 0.79 / 2.0, but a racer's takeoff and turns exceed 15 m/s^2, so the gate threw out the frames that
    were right (4061; d05's first solved frame after takeoff moved from #161 to #179 and the path jumped there).
  Huber on the acceleration prior made it worse (1.6 / 5.4): at 60 fps a 1.5 cm wobble IS ~100 m/s^2, more than the
    flying, so "let large accelerations through" lets the wobble through.
  jerk 300 + acc 40 + gate 1000 (these defaults): 0.60 / 1.61, 4591 frames on their own points, first solved #161.
    Jerk separates them: a racer holds 30 m/s^2 for tenths of a second (~150 m/s^3) while the wobble flips sign
    every frame (tens of thousands of m/s^3). Against the first defaults the path moved 1.4 cm p50, 3-7 cm in turns
    over 500 deg/s, 0.09 deg p50; each frame's own points fit 0.37 px worse p50 (1.95 p90) - the wobble removed.
    Gate 1000: with the jerk term a frame's pose sits further from its own noisy PnP answer; at 250 that alone
    dropped 15% of the frames (3877).
SIG_PX 4: at 2 px the median Mahalanobis^2 was 30-60 for 6 dof (neighbouring matches share their error)
and a quarter of the frames were dropped; 4 px kept 91% and gave mark p90 76 px against 94."""
import json, sys, os, numpy as np, scipy.sparse as sp, scipy.sparse.linalg as spl
from scipy.spatial.transform import Rotation as Rot
E = lambda k, d: float(os.environ.get(k, d))
SIG_PX, SIG_ACC, SIG_ALPHA, MAX_PTS, GATE, HUBER = E("SIG_PX", 4), E("SIG_ACC", 40), E("SIG_ALPHA", 50), int(E("MAX_PTS", 300)), E("GATE", 1000), 3.0
SIG_JERK = E("SIG_JERK", 300)                          # m/s^3 on position jerk (4 frames); 0 = off
FAR_OUT = E("FAR_OUT", 15)                             # m: RANSAC rejects this far or further are used anyway
MIN_INL, TAKEOFF = 100, int(E("TAKEOFF", 0))            # hdz_0067 from the old chain: TAKEOFF=116 (its ground frames had no matches)
init = json.load(open(sys.argv[1])); out_path = sys.argv[-1]
cam = json.load(open(os.environ.get("CAM") or os.path.join(os.path.dirname(os.path.abspath(sys.argv[1])), "dvr_pinhole.mp4.json")))
fx, fy, cx, cy = cam["fx"], cam["fy"], cam["cx"], cam["cy"]; dt = 1 / init["fps"]
rng = np.random.default_rng(0); obs = {}
STRATA = [0, 5, 15, 40, 1e9]
def sample(X, uv, inl, c):
    """MAX_PTS points per frame, in equal quotas per distance band (a band short of points hands its quota on).
    Drawn uniformly, a frame over grass gives the near ground ~99% of the points; its pose then fits the ground and
    trades a position error for a rotation error that only far objects show (d05 #293: poles 15-20 px off with the
    arch in place). Far matches the tracker's RANSAC rejected (FAR_OUT m and beyond) come back: that consensus was the
    near ground's, and they disagree with it by exactly that error. Near rejects stay out - those are mismatches."""
    dist = np.linalg.norm(X - c, axis=1); keep = inl | (dist >= FAR_OUT)
    X, uv, dist = X[keep], uv[keep], dist[keep]
    groups = [np.flatnonzero((dist >= lo) & (dist < hi)) for lo, hi in zip(STRATA[:-1], STRATA[1:])]
    out, left = [], MAX_PTS
    for n_, g in enumerate(sorted(groups, key=len)):                 # smallest band first, so its leftover moves up
        q = left // (len(groups) - n_); take = g if len(g) <= q else rng.choice(g, q, replace=False)
        out.append(take); left -= len(take)
    k = np.concatenate(out); return X[k], uv[k]
for pair in sys.argv[2:-1]:
    jl, dd = pair.split(":")
    for r in map(json.loads, open(jl)):
        f = os.path.join(dd, f"{r['i']:05d}.npz")
        if r.get("inliers", 0) >= MIN_INL and r["i"] >= TAKEOFF and os.path.exists(f):
            z = np.load(f); X, uv = z["X"].astype(np.float64), z["uv"].astype(np.float64)
            X, uv = sample(X, uv, z["inl"] if "inl" in z.files else np.ones(len(X), bool), np.array(r["pos"]))
            obs[r["i"]] = (X, uv, Rot.from_quat(r["quat"]), np.array(r["pos"]), float(np.clip(1 - (r["k"] if r.get("k") is not None else 1), E("AFLOOR", 0.02), 1)))   # no k (the probe did not solve): unknown, so not trusted; the floor keeps a run of k ~ 1 from floating
def marg_t(X, uv, R0, p0):                            # translation information of the frame's PnP, rotation marginalised
    H = frame_terms(R0, p0, X, uv)[0]; return H[3:, 3:] - H[3:, :3] @ np.linalg.solve(H[:3, :3] + 1e-9 * np.eye(3), H[:3, 3:])
idx = sorted(obs); i0, i1 = idx[0], idx[-1]; F = list(range(i0, i1 + 1)); n = len(F)
R = [Rot.from_quat(init["poses"][i]["quat"]) for i in F]; P = np.array([init["poses"][i]["pos"] for i in F], float)
def frame_terms(Ri, pi, X, uv):                      # Huber-weighted reprojection: H (6x6), g (6), chi2, n
    Rt = Ri.as_matrix().T; d = (X - pi) @ Rt.T; z = d[:, 2]; ok = z > 0.1
    d, uv, z = d[ok], uv[ok], z[ok]
    u = np.c_[fx * d[:, 0] / z + cx, fy * d[:, 1] / z + cy]; r = (u - uv) / SIG_PX
    a = np.linalg.norm(r, axis=1); w = np.where(a < HUBER, 1.0, HUBER / np.maximum(a, 1e-9))
    Ju = np.zeros((len(d), 2, 3)); Ju[:, 0, 0] = fx / z; Ju[:, 0, 2] = -fx * d[:, 0] / z**2; Ju[:, 1, 1] = fy / z; Ju[:, 1, 2] = -fy * d[:, 1] / z**2
    dx = np.zeros((len(d), 3, 3)); dx[:, 0, 1], dx[:, 0, 2], dx[:, 1, 0], dx[:, 1, 2], dx[:, 2, 0], dx[:, 2, 1] = -d[:, 2], d[:, 1], d[:, 2], -d[:, 0], -d[:, 1], d[:, 0]
    J = np.concatenate([Ju @ dx, Ju @ (-Rt)[None]], axis=2) / SIG_PX          # (m,2,6): d(residual)/d(dtheta_body, dp)
    Jw = J * w[:, None, None]
    return np.einsum("mki,mkj->ij", Jw, J), np.einsum("mki,mk->i", Jw, r), float(np.sum(w * a * a)), len(d)
It = {i: marg_t(*obs[i][:4]) for i in idx}
def solve(active, iters=30):
    global R, P
    for it in range(iters):
        rows, cols, vals, b = [], [], [], np.zeros(6 * n)
        def add(i, j, M):
            for a_ in range(6):
                rows.extend([6 * i + a_] * 6); cols.extend(range(6 * j, 6 * j + 6)); vals.extend(M[a_])
        for k, i in enumerate(F):
            if i in active:
                # rotation from the frame's own points (position held at its current value), position as a prior at
                # the frame's PnP answer weighted (1 - k)^2: k is the share of that answer that echoes the render
                Hf, gf, _, _ = frame_terms(R[k], P[k], *obs[i][:2]); a2 = obs[i][4] ** 2
                H = np.zeros((6, 6)); H[:3, :3] = Hf[:3, :3]; H[3:, 3:] = a2 * It[i]
                g = np.r_[gf[:3], a2 * It[i] @ (P[k] - obs[i][3])]
                add(k, k, H); b[6 * k:6 * k + 6] -= g
        ca, cr = 1 / (SIG_ACC * dt * dt), 1 / (SIG_ALPHA * dt * dt)
        W = [(Rt.inv() * Rn).as_rotvec() for Rt, Rn in zip(R[:-1], R[1:])]    # body rate per step
        for k in range(1, n - 1):
            e = np.r_[cr * (W[k] - W[k - 1]), ca * (P[k - 1] - 2 * P[k] + P[k + 1])]
            J = [np.zeros((6, 6)) for _ in range(3)]
            J[0][:3, :3], J[1][:3, :3], J[2][:3, :3] = cr * np.eye(3), -2 * cr * np.eye(3), cr * np.eye(3)
            J[0][3:, 3:], J[1][3:, 3:], J[2][3:, 3:] = ca * np.eye(3), -2 * ca * np.eye(3), ca * np.eye(3)
            for a_ in range(3):
                b[6 * (k - 1 + a_):6 * (k + a_)] -= J[a_].T @ e
                for c_ in range(3): add(k - 1 + a_, k - 1 + c_, J[a_].T @ J[c_])
        if SIG_JERK > 0:
            # jerk, not acceleration, separates match noise from flying: a racer holds 30 m/s^2 for tenths of a second
            # (takeoff, turns) but a 1.5 cm frame-to-frame wobble is ~100 m/s^2 flipping sign every frame, i.e. tens of
            # thousands of m/s^3. So this term flattens the wobble while leaving sustained acceleration nearly free.
            cj = 1 / (SIG_JERK * dt ** 3); co = (-1, 3, -3, 1)
            for k in range(1, n - 2):
                e = cj * sum(c * P[k - 1 + a_] for a_, c in enumerate(co))
                for a_ in range(4):
                    b[6 * (k - 1 + a_) + 3:6 * (k + a_)] -= cj * co[a_] * e
                    for c_ in range(4):
                        M = np.zeros((6, 6)); M[3:, 3:] = cj * cj * co[a_] * co[c_] * np.eye(3); add(k - 1 + a_, k - 1 + c_, M)
        A = sp.csc_matrix((vals, (rows, cols)), shape=(6 * n, 6 * n)) + sp.identity(6 * n) * 1e-6
        x = spl.spsolve(A, b).reshape(n, 6)
        # a stretch whose positions are all weighted to ~0 is held only by the acceleration prior: the step can be
        # tens of metres and the rotation linearisation breaks. Cap each frame's step and iterate instead.
        x[:, :3] *= np.minimum(1, 0.1 / np.maximum(np.linalg.norm(x[:, :3], axis=1), 1e-12))[:, None]
        x[:, 3:] *= np.minimum(1, 1.0 / np.maximum(np.linalg.norm(x[:, 3:], axis=1), 1e-12))[:, None]
        R = [Ri * Rot.from_rotvec(x[k, :3]) for k, Ri in enumerate(R)]; P = P + x[:, 3:]
        step = np.abs(x).max()
        if step < 1e-5: break
    return step
active = set(idx)
for rnd in range(6):
    step = solve(active)
    m2 = {}
    for k, i in enumerate(F):
        if i not in active: continue
        X, uv, R0, p0, a_ = obs[i]; Hr = frame_terms(R0, p0, X, uv)[0][:3, :3]
        dr, dp = (R0.inv() * R[k]).as_rotvec(), P[k] - p0; m2[i] = float(dr @ Hr @ dr + a_ ** 2 * dp @ It[i] @ dp)
    bad = {i for i, v in m2.items() if v > GATE}
    print(f"round {rnd}: {len(active)} frames with points, step {step:.1e}, Mahalanobis^2 p50 {np.median(list(m2.values())):.1f} p90 {np.percentile(list(m2.values()), 90):.1f}, over {GATE:g}: {len(bad)}", flush=True)
    if not bad: break
    worst = sorted(bad, key=lambda i: -m2[i])[:max(1, len(bad) // 2)]       # drop the worst half, then solve again
    active -= set(worst)
# fidelity: how much worse each frame's own points fit the solved track than their own PnP answer
drms = []
for k, i in enumerate(F):
    if i in active:
        X, uv, R0, p0, _ = obs[i]; a = frame_terms(R[k], P[k], X, uv); b_ = frame_terms(R0, p0, X, uv)
        drms.append(SIG_PX * (np.sqrt(a[2] / a[3]) - np.sqrt(b_[2] / b_[3])))
print(f"fidelity: reprojection RMS over each frame's own PnP answer p50 {np.median(drms):+.2f} p90 {np.percentile(drms, 90):+.2f} px")
out = list(init["poses"])
for k, i in enumerate(F):
    if out[i] is None: continue
    out[i] = {"i": i, "t": out[i]["t"], "pos": np.round(P[k], 3).tolist(), "quat": np.round(R[k].as_quat(), 6).tolist(), "src": "ba" if i in active else "ba-fill"}
json.dump({**{k: v for k, v in init.items() if k != "poses"}, "poses": out}, open(out_path, "w"))
print(f"{sum(1 for i in F if i in active)} frames on their own points ({sum(1 for i in F if i in active and obs[i][4] < 0.5)} with the position weighted under a quarter: k > 0.5), {n - len(active)} on the motion prior alone")
