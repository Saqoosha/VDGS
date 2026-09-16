#!/usr/bin/env python3
"""
Carry a 3DGS capture's spherical harmonics through the same transform as its geometry.

Until 2026-09-16 every tool here that rotated or mirrored a .ply moved the positions and
the quaternions and left f_rest_* alone, so the view-dependent colour was evaluated from
the wrong side of each gaussian's lobe (numbers in docs/alignment.ja.md). align_ply.py,
fit_transform.py --apply and PlyLoader now go through this module.

The per-band matrix is not written by hand: the shader's basis functions (transcribed
from ShadeSH in GaussianSplatting.hlsl; keep in sync by hand, `--check` verifies the
mirror diagonal) are sampled before and after the transform and the map between them is
solved by least squares. That holds for any orthogonal matrix, reflections included.

    python3 tools/splat_sh.py --check                             # self-test, exits 1 on drift
    python3 tools/splat_sh.py --apply matrix.json in.ply out.ply   # SH only, for a ply whose
    python3 tools/splat_sh.py --mirror y in.ply out.ply            # geometry was moved without it

Library use: rotate_f_rest(f_rest, R) with f_rest (n, 3*b) in the .ply's channel-major
layout (b coefficients per channel, b in 3/8/15 for degree 1/2/3) and R the 3x3 that was
applied to the positions.
"""
import argparse
import json
import sys

import numpy as np

# Constants as in the shader.
SH_C1 = 0.4886025
SH_C2 = [1.0925484, -1.0925484, 0.3153916, -1.0925484, 0.5462742]
SH_C3 = [-0.5900436, 2.8906114, -0.4570458, 0.3731763, -0.4570458, 1.4453057, -0.5900436]


def basis(dirs):
    """The 15 view-dependent basis functions, in f_rest order, for unit directions (n, 3).

    Transcribed from ShadeSH: band 1 is coefficients 0..2, band 2 is 3..7, band 3 is 8..14.
    The shader negates the direction first; that is a fixed linear map and cancels out of
    the transform matrix, so it is omitted here.
    """
    x, y, z = dirs[:, 0], dirs[:, 1], dirs[:, 2]
    xx, yy, zz = x * x, y * y, z * z
    xy, yz, xz = x * y, y * z, x * z
    cols = [
        -SH_C1 * y, SH_C1 * z, -SH_C1 * x,
        SH_C2[0] * xy, SH_C2[1] * yz, SH_C2[2] * (2 * zz - xx - yy), SH_C2[3] * xz, SH_C2[4] * (xx - yy),
        SH_C3[0] * y * (3 * xx - yy), SH_C3[1] * xy * z, SH_C3[2] * y * (4 * zz - xx - yy),
        SH_C3[3] * z * (2 * zz - 3 * xx - 3 * yy), SH_C3[4] * x * (4 * zz - xx - yy),
        SH_C3[5] * z * (xx - yy), SH_C3[6] * x * (xx - 3 * yy),
    ]
    return np.stack(cols, axis=1)


BANDS = [(0, 3), (3, 8), (8, 15)]


def transform_matrix(R, samples=4096, seed=0):
    """The 15x15 block-diagonal D with c_new = D @ c_old for geometry moved by R.

    A direction d in the new frame corresponds to R^T d in the old one, so the new colour
    function is f'(d) = f(R^T d). Per band, Y(R^T d) = Y(d) @ D_l is solved on random
    directions; the fit is exact up to float noise because each band's span is closed
    under O(3).
    """
    R = np.asarray(R, dtype=np.float64)
    if R.shape != (3, 3) or not np.allclose(R @ R.T, np.eye(3), atol=1e-6):
        raise ValueError("R must be a 3x3 orthogonal matrix (rotation or reflection)")
    rng = np.random.default_rng(seed)
    d = rng.normal(size=(samples, 3))
    d /= np.linalg.norm(d, axis=1, keepdims=True)
    A = basis(d)
    B = basis(d @ R)          # rows are (R^T d_i)^T = d_i^T R
    D = np.zeros((15, 15))
    for lo, hi in BANDS:
        Dl, *_ = np.linalg.lstsq(A[:, lo:hi], B[:, lo:hi], rcond=None)
        resid = np.abs(A[:, lo:hi] @ Dl - B[:, lo:hi]).max()
        if resid > 1e-6:
            raise RuntimeError(f"band {lo}-{hi}: basis fit residual {resid:.2e}; R is not orthogonal?")
        D[lo:hi, lo:hi] = Dl
    return D


PER_CHANNEL = {3: 1, 8: 2, 15: 3}   # coefficients per channel -> SH degree


def f_rest_columns(props):
    """Column indices of f_rest_0..N-1 for a degree-1/2/3 ply; [] when there is no SH.

    Exits on a count that is not 9, 24 or 45 - moving the geometry and leaving such a
    file's SH behind is exactly the bug this module exists to fix.
    """
    n = sum(1 for p in props if p.startswith("f_rest_"))
    if n == 0:
        return []
    if n // 3 not in PER_CHANNEL or n % 3:
        sys.exit(f"f_rest_* count {n} is not a full SH degree (9, 24 or 45)")
    return [props.index(f"f_rest_{k}") for k in range(n)]


def rotate_f_rest(f_rest, R):
    """Apply D to (n, 3*b) f_rest in the .ply's channel-major layout. Returns a new array."""
    b = f_rest.shape[1] // 3
    if b not in PER_CHANNEL or f_rest.shape[1] % 3:
        raise ValueError(f"f_rest has {f_rest.shape[1]} columns; expected 9, 24 or 45")
    D = transform_matrix(R)[:b, :b]     # block-diagonal, so the leading bands stand alone
    out = np.empty_like(f_rest)
    for c in range(3):
        blk = f_rest[:, c * b:(c + 1) * b].astype(np.float64)
        out[:, c * b:(c + 1) * b] = (blk @ D.T).astype(f_rest.dtype)
    return out


MIRROR = {"x": np.diag([-1.0, 1, 1]), "y": np.diag([1.0, -1, 1]), "z": np.diag([1.0, 1, -1])}


def _read_ply(path):
    with open(path, "rb") as f:
        head = b""
        while b"end_header\n" not in head:
            chunk = f.read(4096)
            if not chunk:
                sys.exit(f"{path}: no end_header")
            head += chunk
    idx = head.index(b"end_header\n") + len(b"end_header\n")
    header = head[:idx].decode("ascii")
    lines = header.splitlines()
    props = [l.split()[-1] for l in lines if l.startswith("property")]
    types = {l.split()[1] for l in lines if l.startswith("property")}
    if types != {"float"}:
        sys.exit(f"{path}: only all-float32 .ply is handled (found {sorted(types)})")
    n = [int(l.split()[-1]) for l in lines if l.startswith("element vertex")][0]
    data = np.fromfile(path, dtype=np.float32, offset=idx, count=n * len(props)).reshape(n, len(props))
    return header, props, data


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true",
                    help="self-test: the mirror-y diagonal against the hand-derived signs; exit 1 on drift")
    ap.add_argument("--apply", metavar="MATRIX.json", help="fit_transform.py output; only its R is used")
    ap.add_argument("--mirror", choices=list(MIRROR), help="instead of --apply: a single axis mirror")
    ap.add_argument("ply_in", nargs="?")
    ap.add_argument("ply_out", nargs="?")
    args = ap.parse_args()

    if args.check:
        D = transform_matrix(MIRROR["y"])
        np.set_printoptions(precision=2, suppress=True, linewidth=160)
        expect = np.ones(15)
        expect[[0, 3, 4, 8, 9, 10]] = -1          # the basis functions odd in y; PlyLoader.cs uses the same list
        print("mirror y -> diagonal of D:")
        print(np.diag(D))
        off = np.abs(D - np.diag(np.diag(D))).max()
        print(f"largest off-diagonal entry {off:.1e}")
        rz = np.array([[-1.0, 0, 0], [0, -1.0, 0], [0, 0, 1.0]])
        print("rotate 180 about z -> diagonal:")
        print(np.diag(transform_matrix(rz)))
        ok = np.allclose(np.diag(D), expect, atol=1e-9) and off < 1e-9
        print("OK" if ok else "MISMATCH: basis() has drifted from ShadeSH")
        sys.exit(0 if ok else 1)

    if not (args.ply_in and args.ply_out) or not (args.apply or args.mirror):
        ap.error("need --apply MATRIX.json or --mirror AXIS, plus ply_in ply_out")
    if args.apply:
        R = np.asarray(json.load(open(args.apply))["R"], dtype=np.float64)
    else:
        R = MIRROR[args.mirror]

    header, props, data = _read_ply(args.ply_in)
    cols = f_rest_columns(props)
    if not cols:
        sys.exit("input carries no SH (f_rest_*); nothing to transform")
    data[:, cols] = rotate_f_rest(data[:, cols], R)
    with open(args.ply_out, "wb") as f:
        f.write(header.encode())
        data.tofile(f)
    print(f"wrote {args.ply_out}  ({data.shape[0]} splats, SH transformed, geometry untouched)")
    print("R =\n" + np.array2string(R, precision=6, suppress_small=True))


if __name__ == "__main__":
    main()
