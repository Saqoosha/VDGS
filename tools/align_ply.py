#!/usr/bin/env python3
"""Find the floor in a 3DGS capture and level it.

A capture straight out of COLMAP sits in an arbitrary frame: the floor is at
some random angle, the origin is wherever the reconstruction happened to land,
and one unit means nothing in particular. Flying that in a sim is miserable -
the whole world is tilted and the wrong size.

Rather than nudging it by eye inside the game, solve it here: detect the floor
plane with RANSAC, rotate its normal onto +Y, drop the floor to y=0, and set the
scale from a known real-world height. The result loads into the sim already
correct, which is why the mod itself has no alignment controls.

    python3 tools/align_ply.py in.ply --detect
    python3 tools/align_ply.py in.ply out.ply --ceiling 2.6
    python3 tools/align_ply.py in.ply out.ply --scale 0.55

Rotations are applied to positions AND to each gaussian's orientation quaternion;
rotating positions alone would leave every splat pointing the wrong way.
"""

import argparse
import struct
import sys

import numpy as np


# ----------------------------------------------------------------- ply io

def read_ply(path):
    with open(path, "rb") as f:
        header = b""
        while not header.endswith(b"end_header\n"):
            c = f.read(1)
            if not c:
                raise SystemExit("not a binary ply (no end_header)")
            header += c

        text = header.decode("ascii", errors="replace")
        if "binary_little_endian" not in text:
            raise SystemExit("only binary_little_endian ply is supported")

        props, count = [], 0
        for line in text.splitlines():
            p = line.split()
            if not p:
                continue
            if p[0] == "element" and p[1] == "vertex":
                count = int(p[2])
            elif p[0] == "property":
                if p[1] != "float":
                    raise SystemExit(f"unsupported property type: {p[1]}")
                props.append(p[2])

        data = np.fromfile(f, dtype=np.float32, count=count * len(props))

    if data.size != count * len(props):
        raise SystemExit(f"truncated: expected {count*len(props)} floats, got {data.size}")
    return header, props, data.reshape(count, len(props))


def write_ply(path, header, rows):
    out = []
    for line in header.decode("ascii").splitlines():
        if line.startswith("element vertex"):
            out.append(f"element vertex {len(rows)}")
        else:
            out.append(line)
    with open(path, "wb") as f:
        f.write(("\n".join(out) + "\n").encode("ascii"))
        rows.astype(np.float32).tofile(f)


# ------------------------------------------------------------- floor plane

def fit_plane(pts):
    """Least-squares plane through points. Returns (normal, point_on_plane)."""
    centroid = pts.mean(axis=0)
    # Smallest singular vector is the normal of the best-fit plane.
    _, _, vh = np.linalg.svd(pts - centroid, full_matrices=False)
    return vh[2], centroid


def detect_floor(xyz, axis, sign, iterations=1500, threshold=0.02, seed=0):
    """
    RANSAC for the floor, given which way is up.

    The up direction is a parameter rather than something to infer. Searching all
    six candidates picks whichever plane has the most inliers, and in a real room
    that is regularly a wall: drjohnson has bounds 7.6 x 4.7 x 10.7 - unmistakably
    Y-up to a human - yet the unconstrained search confidently returned X. One
    glance at the capture settles it, so the glance is the input.
    """
    rng = np.random.default_rng(seed)

    spread = xyz.max(axis=0) - xyz.min(axis=0)
    thr = threshold * float(np.median(spread))

    # Only the bottom slice: ceilings and tabletops are large flat planes too.
    v = xyz[:, axis] * sign
    lo, hi = np.percentile(v, 1), np.percentile(v, 25)
    band = xyz[(v >= lo) & (v <= hi)]
    if len(band) < 100:
        return None

    pool = band if len(band) <= 200_000 else band[rng.choice(len(band), 200_000, replace=False)]

    best_cnt, best = 0, None
    for _ in range(iterations):
        p = pool[rng.choice(len(pool), 3, replace=False)]
        n = np.cross(p[1] - p[0], p[2] - p[0])
        ln = np.linalg.norm(n)
        if ln < 1e-9:
            continue
        n = n / ln

        # Reject anything steeply tilted from the stated up axis: a real floor is
        # within a few degrees, and allowing more lets walls win on inlier count.
        if abs(n[axis]) < 0.98:
            continue

        d = np.abs((pool - p[0]) @ n)
        inl = d < thr
        cnt = int(inl.sum())
        if cnt > best_cnt:
            best_cnt, best = cnt, pool[inl]

    if best is None:
        return None

    # Iterative refinement. A plane through 3 random points is noisy, and fitting
    # once to its inliers is still dragged around by whatever furniture happened to
    # be included. Re-selecting inliers against the improved plane and refitting
    # converges onto the real floor - the difference shows up as a visible tilt in
    # the sim, so one pass is not enough.
    n, p0 = fit_plane(best)
    for _ in range(5):
        d = np.abs((pool - p0) @ n)
        inl = pool[d < thr]
        if len(inl) < 50:
            break
        n_new, p0 = fit_plane(inl)
        # Keep the normal on the same side to avoid flip-flopping between passes.
        if n_new @ n < 0:
            n_new = -n_new
        if np.allclose(n_new, n, atol=1e-6):
            n = n_new
            break
        n = n_new
        best_cnt = len(inl)

    if n[axis] * sign < 0:
        n = -n
    return n, p0, best_cnt


def rotation_to_up(normal):
    """Rotation matrix taking `normal` onto +Y."""
    a = normal / np.linalg.norm(normal)
    b = np.array([0.0, 1.0, 0.0])
    v = np.cross(a, b)
    c = float(a @ b)
    if np.linalg.norm(v) < 1e-9:
        return np.eye(3) if c > 0 else np.diag([1.0, -1.0, -1.0])
    vx = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + vx + vx @ vx * (1.0 / (1.0 + c))


def mat_to_quat(m):
    """Rotation matrix -> (w, x, y, z), matching the ply's rot_0..rot_3 order."""
    t = np.trace(m)
    if t > 0:
        s = np.sqrt(t + 1.0) * 2
        w = 0.25 * s
        x = (m[2, 1] - m[1, 2]) / s
        y = (m[0, 2] - m[2, 0]) / s
        z = (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w = (m[2, 1] - m[1, 2]) / s
        x = 0.25 * s
        y = (m[0, 1] + m[1, 0]) / s
        z = (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w = (m[0, 2] - m[2, 0]) / s
        x = (m[0, 1] + m[1, 0]) / s
        y = 0.25 * s
        z = (m[1, 2] + m[2, 1]) / s
    else:
        s = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w = (m[1, 0] - m[0, 1]) / s
        x = (m[0, 2] + m[2, 0]) / s
        y = (m[1, 2] + m[2, 1]) / s
        z = 0.25 * s
    return np.array([w, x, y, z])


def quat_mul(a, b):
    """Hamilton product, (w,x,y,z) convention."""
    aw, ax, ay, az = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    bw, bx, by, bz = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([
        aw * bw - ax * bx - ay * by - az * bz,
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
    ], axis=-1)


def check_floor_is_down(rows, props):
    """
    Warn when the densest horizontal slice sits in the upper half.

    The floor carries more gaussians than anything else in a room capture, so if
    the density peak is near the top, the whole thing is upside down. This is not
    hypothetical: SuperSplat writes ply with Y inverted relative to Unity, so a
    capture that looks perfectly upright in the editor lands in the game with its
    ceiling on the ground. Nothing else in the pipeline notices.
    """
    y = rows[:, props.index("y")].astype(np.float64)
    lo, hi = np.percentile(y, 0.5), np.percentile(y, 99.5)
    if hi - lo < 1e-6:
        return
    hist, edges = np.histogram(y, bins=20, range=(lo, hi))
    peak = (edges[int(np.argmax(hist))] + edges[int(np.argmax(hist)) + 1]) / 2
    frac = (peak - lo) / (hi - lo)

    print(f"  density check : densest slice at y={peak:.2f} ({frac*100:.0f}% up the range)")
    if frac > 0.6:
        print("  *** WARNING: the densest surface is near the TOP.")
        print("      A room's floor holds the most gaussians, so this is probably")
        print("      upside down. Re-run with --rotate 180,0,0 (SuperSplat exports")
        print("      ply with Y inverted relative to Unity).")


def apply_transform(rows, props, R, floor_y, scale):
    """Rotate, drop the floor to y=0 and scale - positions, orientations and sizes."""
    ix, iy, iz = props.index("x"), props.index("y"), props.index("z")
    out = rows[:, [ix, iy, iz]].astype(np.float64) @ R.T
    out[:, 1] -= floor_y
    out *= scale
    rows[:, [ix, iy, iz]] = out.astype(np.float32)

    # Orientations must follow the same rotation, or every splat ends up skewed
    # while the point cloud looks correct.
    rot_idx = [props.index(f"rot_{i}") for i in range(4)]
    q = rows[:, rot_idx].astype(np.float64)
    norm = np.linalg.norm(q, axis=1, keepdims=True)
    norm[norm == 0] = 1.0
    q /= norm
    rq = mat_to_quat(R)
    rows[:, rot_idx] = quat_mul(np.broadcast_to(rq, q.shape), q).astype(np.float32)

    # Sizes are stored as log, so a multiply becomes an add.
    if abs(scale - 1.0) > 1e-9:
        s_idx = [props.index(f"scale_{i}") for i in range(3)]
        rows[:, s_idx] += np.float32(np.log(scale))


# ----------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input")
    ap.add_argument("output", nargs="?")
    ap.add_argument("--detect", action="store_true", help="report the floor and exit")
    ap.add_argument("--rotate", metavar="X,Y,Z",
                    help="explicit rotation in degrees, applied in X,Y,Z order, instead "
                         "of detecting a floor. Use when detection fails: read the angles "
                         "off a viewer and apply them exactly here.")
    ap.add_argument("--up", default=None, metavar="AXIS",
                    help="which axis points up in the source: y+, y-, x+, x-, z+, z-. "
                         "Look at the capture and say; guessing it from geometry picks "
                         "walls as often as floors.")
    ap.add_argument("--ceiling", type=float,
                    help="real ceiling height in metres; derives the scale factor")
    ap.add_argument("--scale", type=float, help="explicit scale factor")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--flip", action="store_true",
                    help="rotate a further 180 deg about X. Use when the result comes "
                         "out upside down: which side of the floor plane is 'up' cannot "
                         "be decided from the plane alone.")
    ap.add_argument("--sample", type=int, metavar="N",
                    help="keep only N splats (random). For making light preview files "
                         "that a browser viewer can open quickly.")
    args = ap.parse_args()

    header, props, rows = read_ply(args.input)

    if args.sample and args.sample < len(rows):
        rng = np.random.default_rng(args.seed)
        rows = rows[rng.choice(len(rows), args.sample, replace=False)]

    ix, iy, iz = props.index("x"), props.index("y"), props.index("z")
    xyz = rows[:, [ix, iy, iz]].astype(np.float64)

    print(f"input: {len(rows)} splats")
    print(f"  bounds {xyz.min(0).round(2)} .. {xyz.max(0).round(2)}")

    if args.rotate:
        deg = [float(v) for v in args.rotate.split(",")]
        if len(deg) != 3:
            raise SystemExit("--rotate needs three comma-separated angles")
        rx, ry, rz = np.radians(deg)
        Rx = np.array([[1, 0, 0], [0, np.cos(rx), -np.sin(rx)], [0, np.sin(rx), np.cos(rx)]])
        Ry = np.array([[np.cos(ry), 0, np.sin(ry)], [0, 1, 0], [-np.sin(ry), 0, np.cos(ry)]])
        Rz = np.array([[np.cos(rz), -np.sin(rz), 0], [np.sin(rz), np.cos(rz), 0], [0, 0, 1]])
        R = Rz @ Ry @ Rx

        up = xyz @ R.T
        floor_y = float(np.percentile(up[:, 1], 1))
        top_y = float(np.percentile(up[:, 1], 99))
        height = top_y - floor_y
        print(f"\nexplicit rotation {deg} deg")
        print(f"  after rotating: floor y={floor_y:.2f}, p99 y={top_y:.2f}, span={height:.2f} units")

        scale = (args.ceiling / height) if args.ceiling else (args.scale or 1.0)
        print(f"  scale         : {scale:.4f}")

        if args.detect or not args.output:
            span = (up.max(0) - up.min(0)) * scale
            print(f"\nresulting size: {span[0]:.1f} x {span[1]:.1f} x {span[2]:.1f} m")
            if not args.output:
                print("(no output path given; nothing written)")
            return

        apply_transform(rows, props, R, floor_y, scale)
        check_floor_is_down(rows, props)
        write_ply(args.output, header, rows)
        final = rows[:, [ix, iy, iz]]
        print(f"  bounds {final.min(0).round(2)} .. {final.max(0).round(2)}")
        print(f"\nwrote {args.output}")
        return

    if not args.up:
        span = xyz.max(0) - xyz.min(0)
        guess = "xyz"[int(np.argmin(span))]
        raise SystemExit(
            f"\n--up is required. Open the capture in a viewer and say which axis is up.\n"
            f"  axis spans: x={span[0]:.1f} y={span[1]:.1f} z={span[2]:.1f}\n"
            f"  (the smallest span is often - but not reliably - the height: '{guess}')\n"
            f"  e.g. --up y-")

    axis = "xyz".index(args.up[0].lower())
    sign = -1 if args.up.endswith("-") else 1

    print(f"\ndetecting floor (RANSAC, up = {args.up})...")
    found = detect_floor(xyz, axis, sign, seed=args.seed)
    if not found:
        raise SystemExit("no floor plane found - wrong --up, or the capture has no flat ground")

    normal, point, inliers = found
    tilt = np.degrees(np.arccos(min(1.0, abs(float(normal[axis])))))
    print(f"  normal        : {normal.round(4)}")
    print(f"  tilt from {args.up}  : {tilt:.1f} deg")
    print(f"  inliers       : {inliers:,}")

    R = rotation_to_up(normal)
    if args.flip:
        # 180 deg about X: keeps the floor horizontal, swaps which way is up.
        R = np.array([[1.0, 0, 0], [0, -1.0, 0], [0, 0, -1.0]]) @ R
    up = xyz @ R.T
    floor_y = float(np.percentile(up[:, 1], 1))
    top_y = float(np.percentile(up[:, 1], 99))
    height = top_y - floor_y
    print(f"  after leveling: floor y={floor_y:.2f}, p99 y={top_y:.2f}, span={height:.2f} units")

    if args.ceiling:
        scale = args.ceiling / height
        print(f"  scale         : {scale:.4f}  ({height:.2f} units -> {args.ceiling} m)")
    elif args.scale:
        scale = args.scale
        print(f"  scale         : {scale:.4f} (given)")
    else:
        scale = 1.0
        print("  scale         : 1.0 (pass --ceiling or --scale to change)")

    if args.detect or not args.output:
        span = (up.max(0) - up.min(0)) * scale
        print(f"\nresulting size: {span[0]:.1f} x {span[1]:.1f} x {span[2]:.1f} m")
        if not args.output:
            print("(no output path given; nothing written)")
        return

    print("\napplying...")
    apply_transform(rows, props, R, floor_y, scale)
    check_floor_is_down(rows, props)
    write_ply(args.output, header, rows)

    final = rows[:, [ix, iy, iz]]
    print(f"  bounds {final.min(0).round(2)} .. {final.max(0).round(2)}")
    print(f"\nwrote {args.output}")


if __name__ == "__main__":
    main()
