#!/usr/bin/env python3
"""Generate a 3DGS .ply whose splat orientations are known exactly.

Real captures cannot tell you whether each gaussian's rotation survived a
conversion: they look like photographs either way, and a wrong quaternion shows
up only as a vague smearing. This scene is built so the answer is unambiguous.

Every splat is a long thin needle - 20:1 - pointing along a known direction:

    red    needles along +X
    green  needles along +Y
    blue   needles along +Z
    yellow needles along the diagonal (1,1,1), normalised

Each colour is placed in its own row, plus one "fan" of needles rotating in the
XY plane at 15 degree steps to catch rotations that are subtly off rather than
axis-swapped.

If a conversion mangles the orientation, the needles stop pointing where their
colour says they should - and a mirrored scene turns the fan's rotation
direction backwards, which no amount of squinting at a bicycle will reveal.
"""

import argparse
import math
import struct

C0 = 0.28209479177387814
SH_REST = 45


def to_f_dc(c):
    return (c - 0.5) / C0


def logit(p):
    p = min(max(p, 1e-6), 1 - 1e-6)
    return math.log(p / (1 - p))


def quat_from_axis_angle(axis, angle):
    """(w, x, y, z) for a rotation of `angle` about `axis`."""
    n = math.sqrt(sum(c * c for c in axis))
    ax, ay, az = (c / n for c in axis)
    s = math.sin(angle / 2)
    return (math.cos(angle / 2), ax * s, ay * s, az * s)


def quat_aligning_x_to(target):
    """Rotation taking +X onto `target` - the needle's long axis is X."""
    n = math.sqrt(sum(c * c for c in target))
    t = [c / n for c in target]
    x = [1.0, 0.0, 0.0]

    dot = t[0]
    if dot > 0.999999:
        return (1.0, 0.0, 0.0, 0.0)
    if dot < -0.999999:
        return quat_from_axis_angle((0, 0, 1), math.pi)

    axis = [x[1] * t[2] - x[2] * t[1],
            x[2] * t[0] - x[0] * t[2],
            x[0] * t[1] - x[1] * t[0]]
    return quat_from_axis_angle(axis, math.acos(max(-1.0, min(1.0, dot))))


def needle(pos, direction, colour, length=0.5, thickness=0.025, alpha=0.98):
    """One elongated gaussian pointing along `direction`."""
    w, qx, qy, qz = quat_aligning_x_to(direction)
    r, g, b = colour
    return (
        list(pos)
        + [0.0, 0.0, 0.0]
        + [to_f_dc(r), to_f_dc(g), to_f_dc(b)]
        + [0.0] * SH_REST
        + [logit(alpha)]
        # X is the long axis; the other two stay thin so the direction is obvious.
        + [math.log(length), math.log(thickness), math.log(thickness)]
        + [w, qx, qy, qz]
    )


def build(count_per_row=12, spacing=1.2):
    rows = []

    axes = [
        ((1, 0, 0), (1.0, 0.15, 0.15), 0.0),   # red   -> +X
        ((0, 1, 0), (0.15, 1.0, 0.15), 1.5),   # green -> +Y
        ((0, 0, 1), (0.2, 0.35, 1.0), 3.0),    # blue  -> +Z
        ((1, 1, 1), (1.0, 0.95, 0.2), 4.5),    # yellow-> diagonal
    ]
    for direction, colour, z in axes:
        for i in range(count_per_row):
            rows.append(needle((i * spacing - count_per_row * spacing / 2, 0.0, z),
                               direction, colour))

    # A fan sweeping through the XY plane. Mirroring reverses its sense of rotation,
    # which is impossible to see in a photographic capture.
    for i in range(24):
        a = math.radians(i * 15)
        d = (math.cos(a), math.sin(a), 0.0)
        shade = i / 24.0
        rows.append(needle((0.0, 0.0, 6.5 + i * 0.02), d,
                           (0.3 + shade * 0.7, 0.3, 1.0 - shade * 0.7)))

    # One white needle straight up as an unambiguous "this way is up" marker.
    for i in range(6):
        rows.append(needle((0.0, i * 0.35, 8.0), (0, 1, 0), (1.0, 1.0, 1.0)))

    return rows


PROPS = (
    ["x", "y", "z", "nx", "ny", "nz"]
    + [f"f_dc_{i}" for i in range(3)]
    + [f"f_rest_{i}" for i in range(SH_REST)]
    + ["opacity"]
    + [f"scale_{i}" for i in range(3)]
    + [f"rot_{i}" for i in range(4)]
)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("output")
    args = ap.parse_args()

    rows = build()
    header = "ply\nformat binary_little_endian 1.0\n"
    header += f"element vertex {len(rows)}\n"
    for p in PROPS:
        header += f"property float {p}\n"
    header += "end_header\n"

    packer = struct.Struct("<" + "f" * len(PROPS))
    with open(args.output, "wb") as f:
        f.write(header.encode("ascii"))
        for r in rows:
            assert len(r) == len(PROPS), f"{len(r)} != {len(PROPS)}"
            f.write(packer.pack(*r))

    print(f"wrote {len(rows)} needles to {args.output}")
    print("  red=+X  green=+Y  blue=+Z  yellow=diagonal")
    print("  fan in XY plane (reversed sweep = mirrored)")
    print("  white column pointing up at z=8")


if __name__ == "__main__":
    main()
