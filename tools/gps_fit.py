#!/usr/bin/env python3
"""Solve the COLMAP -> Unity similarity from the drone's own GPS log.

A DJI clip carries a `djmd` metadata track with per-frame latitude, longitude and
barometric altitude (exiftool -ee3 reads it). The frames that went into COLMAP are named
after their source frame index, so every registered camera has a GPS fix, and Umeyama on
(camera centre, ENU position) pairs gives scale, gravity and heading in one closed-form
step - no floor detection, no known-height guess, no SuperSplat session.

    python3 tools/gps_fit.py sparse/0/images.txt --fps 59.94 \\
        --clip a=gps_a.csv --clip b=gps_b.csv --out matrix.json

The CSV is what exiftool -p writes: t,lat,lon,abs_alt,rel_alt,yaw,pitch,roll,shutter.
Frame `a_013335.jpg` is frame 13335 of clip `a`, time 13335/fps; the GPS row nearest that
time (plus a searched offset) is its position.

Two things learned on JDL-2026-R5 are built in:

- The frame<->GPS clock offset is searched, not assumed. An unconstrained affine fit
  absorbs a time lag as anisotropy and returns a plausible residual with the wrong scale;
  the similarity fit's residual minimum over the offset finds the true lag instead.
- The output is in Unity's frame (x east, y up, z north). ENU is right-handed and Unity is
  left-handed, so that permutation has determinant -1: it IS the mirror every 3DGS capture
  needs in Unity, applied once, on purpose. Feed the matrix to fit_transform.py --apply,
  which carries quaternions, log-scales and SH through a reflection correctly.
"""
import argparse
import csv
import json
import math
import sys

import numpy as np


def qvec2R(q):
    w, x, y, z = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
        [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
        [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])


def read_cameras(images_txt):
    """name -> camera centre in COLMAP world, plus the world-space down vector per camera."""
    centres, downs = {}, {}
    with open(images_txt) as f:
        lines = [l for l in f if not l.startswith("#")]
    for i in range(0, len(lines), 2):
        p = lines[i].split()
        q = [float(v) for v in p[1:5]]
        t = np.array([float(v) for v in p[5:8]])
        R = qvec2R(q)
        centres[p[9]] = -R.T @ t
        downs[p[9]] = R.T @ np.array([0.0, 1.0, 0.0])     # COLMAP camera +Y is down
    return centres, downs


def read_gps(path):
    rows = list(csv.DictReader(open(path)))
    t = np.array([float(r["t"]) for r in rows])
    lat = np.array([float(r["lat"]) for r in rows])
    lon = np.array([float(r["lon"]) for r in rows])
    alt = np.array([float(r["rel_alt"]) for r in rows])
    return t, lat, lon, alt


def umeyama(a, b):
    """b ~ s R a + t with det(R) = +1. Returns s, R, t, per-point residual."""
    mu_a, mu_b = a.mean(0), b.mean(0)
    a0, b0 = a - mu_a, b - mu_b
    U, D, Vt = np.linalg.svd(b0.T @ a0 / len(a))
    S = np.eye(3)
    if np.linalg.det(U @ Vt) < 0:
        S[2, 2] = -1
    R = U @ S @ Vt
    s = (D * np.diag(S)).sum() / (a0 ** 2).sum(1).mean()
    t = mu_b - s * R @ mu_a
    resid = np.linalg.norm(s * a @ R.T + t - b, axis=1)
    return s, R, t, resid


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("images_txt")
    ap.add_argument("--clip", action="append", required=True, metavar="PREFIX=CSV")
    ap.add_argument("--fps", type=float, default=59.94)
    ap.add_argument("--offset-range", type=float, default=1.5, help="seconds, +-")
    ap.add_argument("--offset-step", type=float, default=1 / 30)
    ap.add_argument("--out")
    a = ap.parse_args()

    clips = {}
    for spec in a.clip:
        k, path = spec.split("=", 1)
        clips[k] = read_gps(path)
    centres, downs = read_cameras(a.images_txt)

    # ENU origin: mean of every fix across clips (one flight, one origin).
    lat0 = np.mean(np.concatenate([c[1] for c in clips.values()]))
    lon0 = np.mean(np.concatenate([c[2] for c in clips.values()]))
    mx = 111320.0 * math.cos(math.radians(lat0))
    my = 110574.0

    names = sorted(centres)
    frame = []
    for n in names:
        pre, idx = n.split("_", 1)
        frame.append((pre, int(idx.split(".")[0]) / a.fps))
    C = np.array([centres[n] for n in names])

    def enu_at(offset):
        out = np.zeros((len(names), 3))
        for i, (pre, tf) in enumerate(frame):
            t, lat, lon, alt = clips[pre]
            j = np.searchsorted(t, tf + offset)
            j = min(max(j, 0), len(t) - 1)
            if j > 0 and abs(t[j - 1] - (tf + offset)) < abs(t[j] - (tf + offset)):
                j -= 1
            out[i] = [(lon[j] - lon0) * mx, (lat[j] - lat0) * my, alt[j]]
        return out

    best = None
    for off in np.arange(-a.offset_range, a.offset_range + 1e-9, a.offset_step):
        E = enu_at(off)
        s, R, t, r = umeyama(C, E)
        # robust passes: drop fixes that disagree with the bulk (GPS under the trees at
        # the pit, hover jitter), refit, repeat until the inlier set settles
        keep = np.ones(len(C), bool)
        for _ in range(4):
            s, R, t, _ = umeyama(C[keep], E[keep])
            r = np.linalg.norm(s * C @ R.T + t - E, axis=1)
            keep = r < 2.5 * np.median(r[keep])
        r2 = r[keep]
        score = r2.mean()
        if best is None or score < best[0]:
            best = (score, off, s, R, t, keep.sum(), r2)
    score, off, s, R, t, nkeep, r2 = best
    print(f"offset {off:+.3f}s  scale {s:.5f} m/unit  residual mean {score:.3f} m  "
          f"p90 {np.percentile(r2, 90):.3f}  inliers {nkeep}/{len(names)}")

    # Sanity 1: unconstrained affine at the chosen offset - its row norms should agree
    # with the similarity scale and be mutually orthogonal, else the lag is still wrong.
    E = enu_at(off)
    A = np.c_[C, np.ones(len(C))]
    M, *_ = np.linalg.lstsq(A, E, rcond=None)
    Mr = M[:3].T
    norms = np.linalg.norm(Mr, axis=1)
    print(f"affine check: row norms {norms.round(4)}  ratio max/min {norms.max() / norms.min():.3f}"
          f"  (similarity {s:.4f})")

    # Sanity 2: the camera down vectors should point along -up in ENU.
    D = np.array([downs[n] for n in names])
    up_from_cams = -(R @ D.T).T.mean(0)
    up_from_cams /= np.linalg.norm(up_from_cams)
    ang = math.degrees(math.acos(np.clip(up_from_cams @ np.array([0, 0, 1.0]), -1, 1)))
    print(f"camera-down vs GPS up: {ang:.1f} deg (drone cameras look down; expect < ~30)")

    # ENU (x east, y north, z up) -> Unity (x east, y up, z north). det = -1 on purpose.
    P = np.array([[1, 0, 0], [0, 0, 1], [0, 1, 0]], dtype=float)
    R_u = P @ R
    t_u = P @ t
    print(f"unity: det {np.linalg.det(R_u):+.0f}  t {t_u.round(2)}")
    if a.out:
        json.dump({"scale": float(s), "R": R_u.tolist(), "t": t_u.tolist(),
                   "offset_s": float(off), "residual_m": float(score),
                   "origin": {"lat": float(lat0), "lon": float(lon0)}},
                  open(a.out, "w"), indent=2)
        print(f"wrote {a.out}")


if __name__ == "__main__":
    main()
