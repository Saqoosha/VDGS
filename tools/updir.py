#!/usr/bin/env python3
"""Which way is up, per capture, by align_ply.py's own test.

    python3 tools/updir.py build/testdata/*.ply

align_ply already carries this check - the floor holds more gaussians than anything else
in a room, so a densest horizontal slice sitting high in the range means the cloud is
upside down - but it only runs while writing a file, so it cannot be used to decide
whether to write one. Same computation as a query, on each ply both as-is and mirrored.

reprocess.sh and preview.sh each keep a hand-written per-scene mirror table, and there is
a third copy in SplatCollision.Attach. This is where those verdicts come from - re-run it
when a capture is added. Measured 2026-08-19:

  playroom-nocrop    as-is  2.5%   mirrored 97.5%   -> must NOT be mirrored
  drjohnson-aligned  as-is 97.5%   mirrored  2.5%   -> must be mirrored
  bonsai2-aligned    as-is 97.5%   mirrored  2.5%   -> must be mirrored
  calico-lod3        as-is 97.5%   mirrored  2.5%   -> must be mirrored
  textilni-lod3      as-is 42.5%   mirrored 57.5%   -> weak, prefers as-is
  luigi              as-is 57.5%   mirrored 42.5%   -> weak, and it is a figure with
                                                       no floor, so the test does not
                                                       apply at all

The -aligned files are hand work: Saqoosha levelled their floors in SuperSplat. That is
why they still need the mirror - SuperSplat exports ply with Y inverted, so a capture
levelled correctly in the editor lands upside down anyway. The tool agrees with the hand
work rather than contradicting it.

playroom-nocrop is the odd one out: it does not need the mirror, and it is a third the
scale of the other playroom variants, so it did not come off the same route. Mirroring it
again puts its floor on the ceiling, which is exactly what the preview was showing.

Treat a split near 50/50 as no answer rather than a weak answer. A capture with no
dominant floor - a figure, an open field - gives one, and acting on it is a coin flip.
"""
import re, sys, os
import numpy as np

SIZES = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4', 'short': '<i2'}


def peak_fraction(y):
    """Where the densest slice sits in the range, 0 = bottom, 1 = top."""
    lo, hi = np.percentile(y, 0.5), np.percentile(y, 99.5)
    if hi - lo < 1e-6:
        return None
    hist, edges = np.histogram(y, bins=20, range=(lo, hi))
    i = int(np.argmax(hist))
    peak = (edges[i] + edges[i + 1]) / 2
    return (peak - lo) / (hi - lo)


for path in sys.argv[1:]:
    with open(path, 'rb') as f:
        head = b''
        while b'end_header' not in head:
            head += f.read(1 << 16)
    end = head.index(b'end_header') + len(b'end_header\n')
    text = head[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', text).group(1))
    dt = np.dtype([(nm, SIZES[k]) for k, nm in re.findall(r'property (\w+) (\w+)', text)])
    rows = np.memmap(path, dtype=dt, mode='r', offset=end, shape=(n,))
    y = np.asarray(rows['y'], dtype=np.float64)

    a = peak_fraction(y)
    m = peak_fraction(-y)
    if a is None:
        print(f'{os.path.basename(path):24} flat - no answer')
        continue

    # Below this the two orientations are indistinguishable and the capture probably has
    # no floor to find. Saying "weak" beats reporting a verdict nobody should act on.
    if abs(a - m) < 0.25:
        verdict = 'WEAK - no dominant floor, decide by eye'
    else:
        verdict = 'as-is' if a < m else 'MIRROR'
    print(f'{os.path.basename(path):24} as-is {a*100:5.1f}%   mirrored {m*100:5.1f}%   '
          f'floor-down: {verdict}')
