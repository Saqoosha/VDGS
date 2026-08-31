#!/usr/bin/env python3
"""Drop the long thin gaussians that render as spikes, and nothing else.

A sliver is not the same thing as a big splat. Walls and floors come out of 3DGS as
plates - two long axes and one short - and cutting those takes the scene with them; what
shows as a spike through a roof is a needle: one long axis and two short ones. The middle
axis is what separates them, in log space so the ratio is scale-free:

    t = (log(mid) - log(min)) / (log(max) - log(min))     t≈0 needle, t≈1 plate

Only needles that are also long and not transparent can be seen, so all three conditions
have to hold before anything is removed.

    python3 cut_needles.py in.ply out.ply --max-length 1.0
    python3 cut_needles.py in.ply --max-length 1.0            # report only
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
    ap.add_argument("src")
    ap.add_argument("out", nargs="?")
    ap.add_argument("--max-length", type=float, default=1.0,
                    help="metres; a needle longer than this goes")
    ap.add_argument("--needle-t", type=float, default=0.25,
                    help="upper bound on t for something to count as a needle")
    ap.add_argument("--min-opacity", type=float, default=0.05)
    args = ap.parse_args()

    props, data, header = read(args.src)
    cols = {k: i for i, k in enumerate(props)}
    s = np.sort(np.exp(data[:, [cols[f"scale_{i}"] for i in range(3)]].astype(np.float64)),
                axis=1)
    lo, mid, hi = np.log(s[:, 0]), np.log(s[:, 1]), np.log(s[:, 2])
    t = (mid - lo) / np.maximum(hi - lo, 1e-9)
    opacity = 1.0 / (1.0 + np.exp(-data[:, cols["opacity"]]))

    drop = (s[:, 2] > args.max_length) & (t < args.needle_t) & (opacity > args.min_opacity)
    print(f"{args.src.split('/')[-1]}: {len(data)} splats, "
          f"{drop.sum()} needles over {args.max_length} m ({100 * drop.mean():.4f}%)")
    if drop.any():
        print(f"  longest removed {s[drop][:, 2].max():.1f} m, "
              f"longest kept {s[~drop][:, 2].max():.1f} m")
    if not args.out:
        return

    keep = ~drop
    with open(args.out, "wb") as f:
        f.write(header.replace(f"element vertex {len(data)}",
                               f"element vertex {keep.sum()}").encode())
        data[keep].astype(np.float32).tofile(f)
    print(f"-> {args.out}  {keep.sum()} splats")


if __name__ == "__main__":
    main()
