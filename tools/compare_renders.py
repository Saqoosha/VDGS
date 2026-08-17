#!/usr/bin/env python3
"""Compare our Unity splat render against an independent WebGL renderer, pixel by pixel.

Grading a splat renderer by eye does not work. Every orientation and format bug in this
project survived visual review - a mirrored scene, a stale chunk buffer and an
orthographic camera each produced an image that looked like a plausible capture. The
only reliable check is a second implementation fed the same .ply from the same camera.

The two renderers do not agree on handedness, so the images may differ by a flip before
they can be subtracted. Rather than deriving which one, every element of the dihedral
group is tried and the best is reported: the winning transform IS the answer to "how do
these two conventions relate", measured instead of argued.

    python3 tools/compare_renders.py <ours.png> <reference.png> [--out diff.png]

Reported numbers:
  coverage IoU  - agreement on which pixels are occupied at all. Catches geometry,
                  orientation and scale errors. This is the number that matters.
  mean |delta|  - average per-channel difference over the union of occupied pixels,
                  0-255. Sensitive to tone mapping and colour space, which the two
                  renderers genuinely differ on, so a nonzero value is expected.
"""

import argparse
import itertools

import numpy as np
from PIL import Image


def load(path):
    img = Image.open(path).convert("RGB")
    return np.asarray(img).astype(np.float64)


# The eight ways to reorient an image: flips combined with a transpose.
def variants(a):
    for name, arr in (
        ("identity", a),
        ("flip-y", a[::-1]),
        ("flip-x", a[:, ::-1]),
        ("rot180", a[::-1, ::-1]),
    ):
        yield name, arr
    t = a.transpose(1, 0, 2)
    for name, arr in (
        ("transpose", t),
        ("rot90", t[::-1]),
        ("rot270", t[:, ::-1]),
        ("transpose-anti", t[::-1, ::-1]),
    ):
        yield name, arr


def score(ours, ref, threshold):
    """Coverage IoU plus mean absolute difference over the union of occupied pixels."""
    a = ours.max(2) > threshold
    b = ref.max(2) > threshold
    union = a | b
    iou = (a & b).sum() / max(union.sum(), 1)
    delta = np.abs(ours - ref).mean(2)
    mad = delta[union].mean() if union.any() else 0.0
    return iou, mad


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ours")
    ap.add_argument("reference")
    ap.add_argument("--out", help="write a side-by-side plus difference image here")
    ap.add_argument("--threshold", type=float, default=12.0,
                    help="channel value above which a pixel counts as occupied")
    args = ap.parse_args()

    ours = load(args.ours)
    ref = load(args.reference)
    if ours.shape != ref.shape:
        raise SystemExit(f"sizes differ: {ours.shape} vs {ref.shape}")

    results = []
    for name, cand in variants(ref):
        iou, mad = score(ours, cand, args.threshold)
        results.append((iou, mad, name, cand))
    results.sort(key=lambda r: -r[0])

    print(f"ours      : {args.ours}")
    print(f"reference : {args.reference}")
    print(f"{ours.shape[1]}x{ours.shape[0]}\n")
    print("  orientation        coverage IoU   mean |delta|")
    for iou, mad, name, _ in results:
        print(f"  {name:<16}   {iou:11.4f}   {mad:12.2f}")

    # Coverage alone cannot separate orientations of a roughly symmetric subject - a
    # human figure scores nearly as well upside down as the right way up. Colour breaks
    # the tie, because a symmetric silhouette is not a symmetric image.
    close = [r for r in results if results[0][0] - r[0] < 0.05]
    if len(close) > 1:
        close.sort(key=lambda r: r[1])
        print(f"\n  {len(close)} orientations within 0.05 IoU - the subject is close to "
              "symmetric, so ranking those by colour instead")
    best_iou, best_mad, best_name, best = close[0]

    print()
    print(f"best: {best_name}  IoU {best_iou:.4f}  mean |delta| {best_mad:.2f}")
    if len(close) > 1 and close[1][1] - best_mad < 2.0:
        print("  !! the runner-up is nearly as good on colour too; this pair cannot "
              "identify the convention. Compare a scene that is not symmetric.")

    if args.out:
        # Amplify the difference: at 1x, a real mismatch and a tone-mapping difference
        # look equally close to black.
        diff = np.clip(np.abs(ours - best) * 3.0, 0, 255)
        strip = np.concatenate([ours, best, diff], axis=1).astype(np.uint8)
        Image.fromarray(strip).save(args.out)
        print(f"\nwrote {args.out}  (ours | reference[{best_name}] | 3x difference)")

    # A genuinely matching pair sits well above 0.9; anything under 0.8 means the two
    # are not showing the same thing, whatever the picture looks like.
    return 0 if best_iou >= 0.8 else 1


if __name__ == "__main__":
    raise SystemExit(main())
