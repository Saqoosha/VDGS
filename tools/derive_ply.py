"""Which testdata files are reproducible from the ones being kept? Checked on POINTS.

The first version of this compared bounding boxes and called drjohnson-final a pure
translation of drjohnson-aligned. It is actually Y and Z both negated. Both produce the
same box, because a box cannot tell a reflection from a translation - the same blindness
that has cost this project a day more than once.

So: same splat count, then take a sample of actual rows, apply each candidate sign
combination plus the shift its bounds imply, and require the points themselves to land on
top of each other. Row order is preserved by every transform tool in this pipeline, so a
direct row-to-row comparison is valid and exact.

    python3 tools/derive_ply.py --keep scenes/*.ply raw/*.ply -- work/*.ply
"""
import itertools, re, sys, os
import numpy as np

SIZES = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'int': '<i4', 'short': '<i2'}
SAMPLE = 20000


def load(path):
    with open(path, 'rb') as f:
        head = b''
        while b'end_header' not in head:
            c = f.read(1 << 16)
            if not c:
                return None
            head += c
    end = head.index(b'end_header') + len(b'end_header\n')
    text = head[:end].decode('ascii', 'replace')
    n = int(re.search(r'element vertex (\d+)', text).group(1))
    dt = np.dtype([(nm, SIZES[k]) for k, nm in re.findall(r'property (\w+) (\w+)', text)])
    rows = np.memmap(path, dtype=dt, mode='r', offset=end, shape=(n,))
    return n, rows


def xyz_at(rows, idx):
    r = rows[idx]
    return np.stack([r['x'], r['y'], r['z']], 1).astype(np.float64)


args = sys.argv[1:]
split = args.index('--')
keep_paths = [a for a in args[:split] if a != '--keep']
test_paths = args[split + 1:]

keep = {}
for p in keep_paths:
    r = load(p)
    if r:
        keep.setdefault(r[0], []).append((p, r[1]))

for p in test_paths:
    r = load(p)
    if r is None:
        print(f'{os.path.basename(p):26} unreadable'); continue
    n, rows = r
    size = os.path.getsize(p) / 1e6
    cands = keep.get(n, [])
    if not cands:
        print(f'{os.path.basename(p):26} {n:>9,} {size:7.0f}MB  no file of that count -> KEEP')
        continue

    idx = np.linspace(0, n - 1, min(SAMPLE, n)).astype(np.int64)
    mine = xyz_at(rows, idx)

    best = None
    for src, srows in cands:
        theirs = xyz_at(srows, idx)
        for sign in itertools.product([1, -1], repeat=3):
            t = theirs * np.array(sign)
            shift = np.median(mine - t, axis=0)
            err = np.abs(mine - (t + shift)).max()
            if best is None or err < best[0]:
                best = (err, src, sign, shift)

    err, src, sign, shift = best
    tag = 'identity' if sign == (1, 1, 1) else f'sign {sign}'
    moved = '' if np.abs(shift).max() < 1e-4 else f' + shift {np.round(shift, 3)}'
    verdict = 'DELETABLE' if err < 1e-3 else 'KEEP'
    print(f'{os.path.basename(p):26} {n:>9,} {size:7.0f}MB  vs {os.path.basename(src):24} '
          f'{tag}{moved}  point residual {err:.5f}  -> {verdict}')
