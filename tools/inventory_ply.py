"""What is actually in each ply, so the folder can be organised on facts.

The ply files with names like -aligned, -final, -nocrop, -up-manual and -mirrorY, and no
record of which transform produced which. Names are not evidence; splat count and bounds
are. Files that share a count are the same cloud under some transform, and the bounds say
which transform.

    python3 tools/inventory_ply.py dir/*.ply
"""
import re, sys, os
import numpy as np

SIZES = {'float': '<f4', 'double': '<f8', 'uchar': 'u1', 'char': 'i1',
         'int': '<i4', 'uint': '<u4', 'short': '<i2', 'ushort': '<u2'}


def info(path):
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
    props = re.findall(r'property (\w+) (\w+)', text)
    dt = np.dtype([(nm, SIZES[k]) for k, nm in props])
    rows = np.memmap(path, dtype=dt, mode='r', offset=end, shape=(n,))
    xyz = np.stack([rows['x'], rows['y'], rows['z']], 1).astype(np.float64)
    sh = sum(1 for _, nm in props if nm.startswith('f_rest_'))
    return n, xyz.min(0), xyz.max(0), sh


rows = []
for p in sys.argv[1:]:
    r = info(p)
    if r is None:
        print(f'{os.path.basename(p):28} unreadable')
        continue
    n, lo, hi, sh = r
    rows.append((os.path.basename(p), n, lo, hi, sh, os.path.getsize(p)))

# Group by splat count: same count means the same cloud, differently transformed.
rows.sort(key=lambda r: (-r[1], r[0]))
last = None
for name, n, lo, hi, sh, size in rows:
    if n != last:
        print()
        last = n
    ext = hi - lo
    print(f'  {name:28} {n:>9,}  sh{sh:<3} {size/1e6:7.1f}MB  '
          f'min [{lo[0]:7.2f}{lo[1]:8.2f}{lo[2]:8.2f} ]  '
          f'max [{hi[0]:7.2f}{hi[1]:8.2f}{hi[2]:8.2f} ]')
