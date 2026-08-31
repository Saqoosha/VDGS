#!/usr/bin/env python3
"""Recover the similarity transform between two versions of the same capture.

A retrain from the same COLMAP reconstruction lands in the same world frame as the
previous one, so the alignment that was worked out by hand for the old .ply - orientation,
scale, where the ground sits - applies unchanged to the new one. What is missing is the
transform itself, and it cannot be read off index-by-index because the aligned version had
splats deleted and a retrain reorders everything anyway.

So it is estimated: coarse-align by centroid and principal axes, then scaled ICP against a
KD-tree. Both point sets describe the same field, so this converges from a crude start.

    python3 fit_transform.py --src raw.ply --ref aligned.ply --out matrix.json
    python3 fit_transform.py --src new.ply --apply matrix.json --out new-aligned.ply

The residual is printed and is the thing to judge: it should land near the capture's own
noise floor. A residual that stays at metres means the two files are not the same scene,
or not the same world frame, and the answer must not be used.
"""

import argparse
import json
import sys

import numpy as np


def read_ply(path, want_all=False):
    with open(path, "rb") as f:
        head = b""
        while b"end_header" not in head:
            chunk = f.read(4096)
            if not chunk:
                sys.exit(f"{path}: no end_header")
            head += chunk
        idx = head.index(b"end_header\n") + len(b"end_header\n")
        text = head[:idx].decode("ascii", "replace")
    props = [l.split()[-1] for l in text.splitlines() if l.startswith("property")]
    n = [int(l.split()[-1]) for l in text.splitlines() if l.startswith("element vertex")][0]
    data = np.fromfile(path, dtype=np.float32, offset=idx, count=n * len(props))
    data = data.reshape(n, len(props))
    if want_all:
        return props, n, data, text[:idx]
    cols = {k: i for i, k in enumerate(props)}
    return data[:, [cols["x"], cols["y"], cols["z"]]]


def principal_frame(p):
    c = p.mean(axis=0)
    q = p - c
    # Sorted by descending spread, and each axis sign-fixed by the skew of the projection,
    # so the two clouds pick the same direction rather than an arbitrary one.
    _, _, vt = np.linalg.svd(q[np.random.default_rng(0).choice(len(q), min(len(q), 50000),
                                                              replace=False)],
                             full_matrices=False)
    R = vt
    for i in range(3):
        if np.mean((q @ R[i]) ** 3) < 0:
            R[i] = -R[i]
    if np.linalg.det(R) < 0:
        R[2] = -R[2]
    return c, R, np.sqrt((q ** 2).sum(axis=1).mean())


def umeyama(src, dst):
    """Least-squares similarity transform (scale, rotation, translation) src -> dst."""
    mu_s, mu_d = src.mean(axis=0), dst.mean(axis=0)
    s0, d0 = src - mu_s, dst - mu_d
    cov = d0.T @ s0 / len(src)
    U, D, Vt = np.linalg.svd(cov)
    S = np.eye(3)
    if np.linalg.det(U) * np.linalg.det(Vt) < 0:
        S[2, 2] = -1
    R = U @ S @ Vt
    scale = np.trace(np.diag(D) @ S) / (s0 ** 2).sum(axis=1).mean()
    t = mu_d - scale * R @ mu_s
    return scale, R, t


def core(p, lo=2.0, hi=98.0, rounds=2):
    """The part of the cloud that is the scene, without the far floaters.

    A training output keeps whatever the optimiser parked out at infinity, while the
    version it is being matched against was cropped. Fitting one to the other with that
    imbalance in place is what makes a trimmed ICP shrink instead of converge: every
    reduction in scale finds more neighbours for points that have no true partner.
    """
    keep = np.ones(len(p), bool)
    for _ in range(rounds):
        q = np.percentile(p[keep], [lo, hi], axis=0)
        keep &= np.all((p >= q[0]) & (p <= q[1]), axis=1)
    return p[keep]


def fit(src, ref, iters, sample, seed=0):
    from scipy.spatial import cKDTree

    rng = np.random.default_rng(seed)
    src_c, ref_c = core(src), core(ref)
    print(f"  cores: src {len(src_c)} / {len(src)}, ref {len(ref_c)} / {len(ref)}")

    cs, Rs, ss = principal_frame(src_c)
    cr, Rr, sr = principal_frame(ref_c)
    tree = cKDTree(ref_c)
    take = rng.choice(len(src_c), min(sample, len(src_c)), replace=False)
    p = src_c[take]

    # Principal axes fix the frame only up to which axis is which and which way each
    # points, and a capture that is wider than it is tall has two of them nearly equal.
    # So every axis permutation and sign is tried and the honest one wins on residual -
    # cheap, and it removes the one place this could silently pick a mirrored answer.
    best = None
    for perm in ([0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]):
        for sx in (1, -1):
            for sy in (1, -1):
                S = np.diag([sx, sy, 1.0])
                Rp = Rs[perm]
                if np.linalg.det(Rp) < 0:
                    Rp = Rp[[0, 2, 1]]
                R0 = Rr.T @ S @ Rp
                if np.linalg.det(R0) < 0:
                    continue
                sc0 = sr / ss
                t0 = cr - sc0 * R0 @ cs
                d, _ = tree.query(sc0 * (p @ R0.T) + t0, workers=-1)
                r = float(np.median(d))
                if best is None or r < best[0]:
                    best = (r, sc0, R0, t0)
    resid, scale, R, t = best
    print(f"  init: median {resid:.3f}, scale {scale:.5f}")

    for i in range(iters):
        moved = scale * (p @ R.T) + t
        dist, nn = tree.query(moved, workers=-1)
        # An absolute, annealing gate rather than a fixed quantile: a quantile always
        # keeps three quarters of the points no matter how bad the fit, which is the
        # freedom the collapse uses.
        gate = max(np.median(dist) * 3.0, np.percentile(dist, 20))
        keep = dist <= gate
        if keep.sum() < 1000:
            break
        s2, R2, t2 = umeyama(p[keep], ref_c[nn[keep]])
        # A similarity fit between the same scene twice cannot need a big scale step;
        # a large one means the correspondences are wrong, so damp it.
        if not 0.9 < s2 / scale < 1.1:
            s2 = scale * np.clip(s2 / scale, 0.9, 1.1)
        scale, R, t = s2, R2, t2
        if i % 5 == 0 or i == iters - 1:
            print(f"  iter {i:3d}  inliers {keep.sum():6d}  residual {dist[keep].mean():.4f}"
                  f"  scale {scale:.5f}")
    return scale, R, t, float(dist[keep].mean())


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", required=True)
    ap.add_argument("--ref")
    ap.add_argument("--apply")
    ap.add_argument("--out", required=True)
    ap.add_argument("--iters", type=int, default=30)
    ap.add_argument("--sample", type=int, default=60000)
    args = ap.parse_args()

    if args.apply:
        m = json.load(open(args.apply))
        scale, R, t = m["scale"], np.array(m["R"]), np.array(m["t"])
        props, n, data, header = read_ply(args.src, want_all=True)
        cols = {k: i for i, k in enumerate(props)}
        xyz = data[:, [cols["x"], cols["y"], cols["z"]]].astype(np.float64)
        data[:, [cols["x"], cols["y"], cols["z"]]] = (scale * (xyz @ R.T) + t).astype(np.float32)

        # Rotation applies to each gaussian's orientation as well - move only the centres
        # and the cloud looks right while every splat stays tilted the old way.
        #
        # A quaternion can only carry a PROPER rotation, and the alignment chain for these
        # captures contains a mirror, so R often has determinant -1. Feeding that through a
        # quaternion multiply produces garbage orientations and the scene renders as a field
        # of spikes - which is exactly what it did. So split the improper part off:
        #
        #     R = Mz . Rp        Mz = diag(1, 1, -1),  Rp = Mz . R  (proper)
        #
        # rotate by Rp as a quaternion product, then apply the mirror as the component flip
        # it is. Both halves are exact and neither has a singularity.
        mirrored = np.linalg.det(R) < 0
        Rp = np.diag([1.0, 1.0, -1.0]) @ R if mirrored else R

        # The input has to be normalised first - a real 3DGS .ply does not store unit
        # quaternions (this capture averages 1.29), so anything assuming unit length is
        # silently working on a distorted rotation.
        quat = np.stack([data[:, cols[f"rot_{i}"]] for i in range(4)], axis=1).astype(np.float64)
        quat /= np.maximum(np.linalg.norm(quat, axis=1, keepdims=True), 1e-12)

        tr = Rp[0, 0] + Rp[1, 1] + Rp[2, 2]
        if tr > 0:
            k = np.sqrt(1.0 + tr) * 2
            qr = np.array([k / 4, (Rp[2, 1] - Rp[1, 2]) / k,
                           (Rp[0, 2] - Rp[2, 0]) / k, (Rp[1, 0] - Rp[0, 1]) / k])
        else:
            # Branch on the largest diagonal element, which keeps the divisor away from
            # zero for every rotation rather than for most of them.
            i = int(np.argmax([Rp[0, 0], Rp[1, 1], Rp[2, 2]]))
            j, l = (i + 1) % 3, (i + 2) % 3
            k = np.sqrt(1.0 + Rp[i, i] - Rp[j, j] - Rp[l, l]) * 2
            qr = np.empty(4)
            qr[0] = (Rp[l, j] - Rp[j, l]) / k
            qr[1 + i] = k / 4
            qr[1 + j] = (Rp[j, i] + Rp[i, j]) / k
            qr[1 + l] = (Rp[l, i] + Rp[i, l]) / k
        qr /= np.linalg.norm(qr)

        w1, x1, y1, z1 = qr
        w2, x2, y2, z2 = quat.T
        out = np.empty_like(quat)
        out[:, 0] = w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2
        out[:, 1] = w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2
        out[:, 2] = w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2
        out[:, 3] = w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2

        if mirrored:
            # Mirroring z leaves w and z alone and negates the other two. Negating w as
            # well is tempting because q and -q are the same rotation, but that identity
            # needs all four to flip; doing it to w alone leaves positions perfect and
            # every ellipsoid pointing somewhere else.
            out[:, 1] *= -1.0
            out[:, 2] *= -1.0

        out /= np.maximum(np.linalg.norm(out, axis=1, keepdims=True), 1e-12)
        for i in range(4):
            data[:, cols[f"rot_{i}"]] = out[:, i].astype(np.float32)

        # Scales are stored as logs, so a uniform scale is an addition.
        for i in range(3):
            data[:, cols[f"scale_{i}"]] += np.float32(np.log(scale))

        with open(args.out, "wb") as f:
            f.write(header.encode())
            data.astype(np.float32).tofile(f)
        print(f"wrote {args.out}  ({n} splats, scale {scale:.5f})")
        return

    if not args.ref:
        sys.exit("need --ref (to fit) or --apply (to use a fit)")
    src, ref = read_ply(args.src), read_ply(args.ref)
    print(f"src {len(src)} pts, ref {len(ref)} pts")
    scale, R, t, resid = fit(src, ref, args.iters, args.sample)
    json.dump({"scale": float(scale), "R": R.tolist(), "t": t.tolist(),
               "residual": resid}, open(args.out, "w"), indent=2)
    print(f"\nwrote {args.out}   residual {resid:.4f} in reference units")


if __name__ == "__main__":
    main()
