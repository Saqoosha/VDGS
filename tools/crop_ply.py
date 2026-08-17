#!/usr/bin/env python3
"""Trim outlier gaussians ("floaters") out of a 3DGS .ply.

Every 3DGS reconstruction scatters a halo of junk gaussians far outside the scene
it actually captured. They cost memory, they blow the bounding box up, and in a
flight sim they read as debris hanging in mid-air. Cutting them is almost always
the difference between "a room" and "an explosion".

Default is a percentile crop, which needs no knowledge of the scene: keep the
central 98% along each axis and drop the rest. An explicit --bounds is available
when you already know where the interesting part is.

    python3 tools/crop_ply.py in.ply out.ply                  # 1..99 percentile
    python3 tools/crop_ply.py in.ply out.ply --percentile 5    # tighter: 5..95
    python3 tools/crop_ply.py in.ply out.ply --bounds -3,-2,-3,3,2,3
    python3 tools/crop_ply.py in.ply --stats                   # inspect only
"""

import argparse
import struct
import sys

import numpy as np


def read_header(f):
    """Returns (property_names, vertex_count, header_length)."""
    header = b""
    while not header.endswith(b"end_header\n"):
        chunk = f.read(1)
        if not chunk:
            raise ValueError("no end_header found - not a binary ply?")
        header += chunk

    text = header.decode("ascii", errors="replace")
    if "binary_little_endian" not in text:
        raise ValueError("only binary_little_endian ply is supported")

    props, count = [], 0
    for line in text.splitlines():
        parts = line.split()
        if not parts:
            continue
        if parts[0] == "element" and parts[1] == "vertex":
            count = int(parts[2])
        elif parts[0] == "property":
            if parts[1] != "float":
                raise ValueError(f"unsupported property type: {parts[1]}")
            props.append(parts[2])
    return props, count, len(header), header


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input")
    ap.add_argument("output", nargs="?")
    ap.add_argument("--percentile", type=float, default=1.0,
                    help="drop this %% from each end of each axis (default 1)")
    ap.add_argument("--bounds", help="explicit minX,minY,minZ,maxX,maxY,maxZ")
    ap.add_argument("--stats", action="store_true", help="report distribution and exit")
    args = ap.parse_args()

    with open(args.input, "rb") as f:
        props, count, header_len, header = read_header(f)
        data = np.fromfile(f, dtype=np.float32, count=count * len(props))

    if data.size != count * len(props):
        raise SystemExit(f"truncated file: expected {count * len(props)} floats, got {data.size}")

    rows = data.reshape(count, len(props))
    ix, iy, iz = props.index("x"), props.index("y"), props.index("z")
    xyz = rows[:, [ix, iy, iz]]

    def report(tag, pts):
        print(f"{tag}: {len(pts)} splats")
        for name, axis in zip("xyz", range(3)):
            v = pts[:, axis]
            print(f"  {name}: min {v.min():9.2f}  p1 {np.percentile(v,1):9.2f}"
                  f"  median {np.median(v):9.2f}  p99 {np.percentile(v,99):9.2f}"
                  f"  max {v.max():9.2f}")

    report("input", xyz)

    if args.stats:
        return

    if not args.output:
        raise SystemExit("output path required (or pass --stats)")

    if args.bounds:
        b = [float(v) for v in args.bounds.split(",")]
        if len(b) != 6:
            raise SystemExit("--bounds needs 6 comma-separated numbers")
        lo, hi = np.array(b[:3]), np.array(b[3:])
    else:
        p = args.percentile
        lo = np.percentile(xyz, p, axis=0)
        hi = np.percentile(xyz, 100 - p, axis=0)

    print(f"\nkeeping x {lo[0]:.2f}..{hi[0]:.2f}  "
          f"y {lo[1]:.2f}..{hi[1]:.2f}  z {lo[2]:.2f}..{hi[2]:.2f}")

    keep = np.all((xyz >= lo) & (xyz <= hi), axis=1)
    kept = rows[keep]
    print(f"kept {keep.sum()} / {count} ({100.0 * keep.sum() / count:.1f}%), "
          f"dropped {count - keep.sum()}")

    if keep.sum() == 0:
        raise SystemExit("crop removed everything - widen the bounds")

    report("output", kept[:, [ix, iy, iz]])

    # Rewrite the header with the new count, keeping every property as-is.
    out_header = []
    for line in header.decode("ascii").splitlines():
        if line.startswith("element vertex"):
            out_header.append(f"element vertex {int(keep.sum())}")
        else:
            out_header.append(line)
    blob = ("\n".join(out_header) + "\n").encode("ascii")

    with open(args.output, "wb") as f:
        f.write(blob)
        kept.astype(np.float32).tofile(f)

    print(f"\nwrote {args.output}")


if __name__ == "__main__":
    main()
