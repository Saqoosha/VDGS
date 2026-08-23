#!/usr/bin/env python3
"""Take the canopy from one training run and everything below it from another.

Two runs of the same capture, aligned into the same frame, are rarely better in the same
places. Retraining with the sky masked put its gained capacity into the forest and left the
lawn where it was, so the useful move is to keep the flown-and-collided scene on the ground
and lift only the trees out of the new one.

The seam is a band, not a line: inside it each splat is kept with a probability that ramps
with height, so the two clouds cross-fade in density instead of meeting at a visible
horizontal cut. Both files must already share a world frame.

    python3 merge_by_height.py --low v22.ply --high skymask.ply --out merged.ply \
        --band 3 5
"""

import argparse
import sys

import numpy as np


def read(path):
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
    return props, data.reshape(n, len(props)), text[:idx]


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--low", required=True, help="source for everything under the band")
    ap.add_argument("--high", required=True, help="source for everything over the band")
    ap.add_argument("--out", required=True)
    ap.add_argument("--band", nargs=2, type=float, default=[3.0, 5.0],
                    metavar=("Y0", "Y1"), help="height range to cross-fade across")
    ap.add_argument("--keep-low-box", nargs=6, type=float, action="append",
                    metavar=("X0", "Y0", "Z0", "X1", "Y1", "Z1"),
                    help="a box that stays with --low at every height. Buildings live at "
                         "the same height as the canopy, so a pure height rule swaps their "
                         "roofs too - and a roof that was cleaned up by hand in one run "
                         "and not in the other is exactly where the difference shows. "
                         "Repeatable.")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    y0, y1 = args.band
    if y1 <= y0:
        sys.exit("--band needs Y0 < Y1")

    props_l, low, header = read(args.low)
    props_h, high, _ = read(args.high)
    # Same properties, not necessarily in the same order: a .ply that has been through an
    # editor or a hand-written filter often comes back with its columns rearranged, and
    # concatenating on position alone would silently pour rotations into colours.
    if sorted(props_l) != sorted(props_h):
        only_l = set(props_l) - set(props_h)
        only_h = set(props_h) - set(props_l)
        sys.exit(f"property sets differ; only in low: {sorted(only_l)}, "
                 f"only in high: {sorted(only_h)}")
    if props_l != props_h:
        print(f"reordering {args.high.split('/')[-1]} to match {args.low.split('/')[-1]}")
        src = {k: i for i, k in enumerate(props_h)}
        high = high[:, [src[k] for k in props_l]]
    cols = {k: i for i, k in enumerate(props_l)}

    rng = np.random.default_rng(args.seed)

    def p_high(y):
        # Smoothstep rather than a linear ramp: it meets 0 and 1 with zero slope, so the
        # density does not kink at either edge of the band.
        t = np.clip((y - y0) / (y1 - y0), 0.0, 1.0)
        return t * t * (3.0 - 2.0 * t)

    ph_low = p_high(low[:, cols["y"]])
    ph_high = p_high(high[:, cols["y"]])
    for box in (args.keep_low_box or []):
        lo = np.array(box[:3]); hi = np.array(box[3:])
        for data, ph in ((low, ph_low), (high, ph_high)):
            xyz = data[:, [cols["x"], cols["y"], cols["z"]]]
            inside = np.all((xyz >= lo) & (xyz <= hi), axis=1)
            ph[inside] = 0.0
    keep_low = rng.random(len(low)) >= ph_low
    keep_high = rng.random(len(high)) < ph_high
    out = np.concatenate([low[keep_low], high[keep_high]], axis=0)

    with open(args.out, "wb") as f:
        f.write(header.replace(f"element vertex {len(low)}",
                               f"element vertex {len(out)}").encode())
        out.astype(np.float32).tofile(f)

    print(f"low  {args.low.split('/')[-1]:28s} {keep_low.sum():8d} / {len(low)}")
    print(f"high {args.high.split('/')[-1]:28s} {keep_high.sum():8d} / {len(high)}")
    print(f"-> {args.out}  {len(out)} splats, band {y0}-{y1} m")


if __name__ == "__main__":
    main()
