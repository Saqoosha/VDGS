#!/usr/bin/env python3
"""Generate a synthetic 3D Gaussian Splatting .ply for pipeline testing.

Waiting on a real capture to test the conversion + rendering path is a waste of
time: a known-shape synthetic scene is better for a first light anyway, because
anything wrong with axis order, colour decoding or splat scale is obvious at a
glance instead of hiding in photographic noise.

The scene is a coloured cube-frame of gaussians (edges of a cube) plus a solid
floor grid, so orientation and scale are readable from any angle:
    +X = red, +Y = green, +Z = blue

Format follows the INRIA 3DGS convention that UnityGaussianSplatting reads:
    x y z nx ny nz f_dc_0..2 f_rest_0..44 opacity scale_0..2 rot_0..3
all float32, binary little endian.
"""

import argparse
import math
import struct

# SH band-0 constant used by every 3DGS implementation to turn f_dc into colour:
#   colour = 0.5 + C0 * f_dc
C0 = 0.28209479177387814

SH_REST = 45  # 15 coefficients x 3 channels, i.e. SH degree 3


def to_f_dc(c):
    """Linear 0..1 colour -> SH band-0 coefficient."""
    return (c - 0.5) / C0


def logit(p):
    p = min(max(p, 1e-6), 1 - 1e-6)
    return math.log(p / (1 - p))


def splat(x, y, z, colour, scale, alpha=0.95):
    """One gaussian, as the tuple of floats the ply row expects."""
    r, g, b = colour
    return (
        [x, y, z]
        + [0.0, 0.0, 0.0]                       # normals, unused by 3DGS
        + [to_f_dc(r), to_f_dc(g), to_f_dc(b)]  # f_dc
        + [0.0] * SH_REST                       # f_rest
        + [logit(alpha)]                        # opacity (stored as logit)
        + [math.log(scale)] * 3                 # scale (stored as log)
        + [1.0, 0.0, 0.0, 0.0]                  # rotation quaternion, identity
    )


def build_scene(size, step, scale):
    """Cube edges in RGB-by-axis colours, plus a grey floor grid."""
    rows = []
    n = int(size / step)

    # Twelve cube edges. Colour encodes which axis the edge runs along.
    for i in range(n + 1):
        t = i * step
        for a in (0.0, size):
            for b in (0.0, size):
                rows.append(splat(t, a, b, (1.0, 0.15, 0.15), scale))  # along X
                rows.append(splat(a, t, b, (0.15, 1.0, 0.15), scale))  # along Y
                rows.append(splat(a, b, t, (0.2, 0.35, 1.0), scale))   # along Z

    # Floor grid at y=0 so ground level and scale are unmistakable in flight.
    grid_step = step * 4
    g = int(size / grid_step)
    for i in range(g + 1):
        for j in range(g + 1):
            rows.append(splat(i * grid_step, 0.0, j * grid_step, (0.75, 0.75, 0.75), scale * 1.5))

    # A brighter marker at the origin corner: tells you where the pivot is when
    # nudging the scene into place in-game.
    for dx in range(3):
        for dy in range(3):
            for dz in range(3):
                rows.append(splat(dx * scale * 2, dy * scale * 2, dz * scale * 2,
                                  (1.0, 1.0, 0.2), scale * 1.2))
    return rows


PROPS = (
    ["x", "y", "z", "nx", "ny", "nz"]
    + [f"f_dc_{i}" for i in range(3)]
    + [f"f_rest_{i}" for i in range(SH_REST)]
    + ["opacity"]
    + [f"scale_{i}" for i in range(3)]
    + [f"rot_{i}" for i in range(4)]
)


def write_ply(path, rows):
    header = "ply\nformat binary_little_endian 1.0\n"
    header += f"element vertex {len(rows)}\n"
    for p in PROPS:
        header += f"property float {p}\n"
    header += "end_header\n"

    with open(path, "wb") as f:
        f.write(header.encode("ascii"))
        packer = struct.Struct("<" + "f" * len(PROPS))
        for r in rows:
            assert len(r) == len(PROPS), f"{len(r)} != {len(PROPS)}"
            f.write(packer.pack(*r))

    return len(rows)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("output", help="output .ply path")
    ap.add_argument("--size", type=float, default=10.0, help="cube edge length in metres")
    ap.add_argument("--step", type=float, default=0.25, help="spacing between gaussians")
    ap.add_argument("--scale", type=float, default=0.06, help="gaussian radius in metres")
    args = ap.parse_args()

    rows = build_scene(args.size, args.step, args.scale)
    count = write_ply(args.output, rows)
    print(f"wrote {count} splats to {args.output}")
    print(f"  cube {args.size}m, spacing {args.step}m, radius {args.scale}m")
    print("  +X red, +Y green, +Z blue, grey floor grid, yellow origin marker")


if __name__ == "__main__":
    main()
