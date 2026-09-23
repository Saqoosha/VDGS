#!/usr/bin/env bash
# SOG fixtures: a small .ply with real spherical harmonics, its .sog, splat-transform's
# own decode of that .sog, and a two-level streamed SOG.
#
#   bash tools/make_sog_fixtures.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/src/VDGS.Tests/fixtures/sog"
ST="npx -y @playcanvas/splat-transform@3.4.2 -q -w"
rm -rf "$OUT"; mkdir -p "$OUT"
python3 "$ROOT/tools/make_test_ply.py" "$OUT/cube.ply" --size 4 --step 0.25 > /dev/null
# make_test_ply writes zero f_rest; give every splat distinct harmonics so the palette
# and the channel order are actually exercised.
python3 - "$OUT/cube.ply" "$OUT/sh.ply" <<'EOF'
import sys, struct, random
src, dst = sys.argv[1:]
d = open(src, 'rb').read(); i = d.index(b'end_header\n') + 11
props = [l.split()[2] for l in d[:i].decode().splitlines() if l.startswith('property')]
n = int([l for l in d[:i].decode().splitlines() if l.startswith('element vertex')][0].split()[2])
row = len(props) * 4; k = props.index('f_rest_0'); r = random.Random(3)
body = bytearray(d[i:])
for j in range(n):
    struct.pack_into('<45f', body, j * row + k * 4, *[r.uniform(-0.4, 0.4) for _ in range(45)])
open(dst, 'wb').write(d[:i] + body)
EOF
rm "$OUT/cube.ply"
$ST "$OUT/sh.ply" "$OUT/sh.sog"
$ST "$OUT/sh.sog" "$OUT/sh-decoded.ply"
# --decimate must be the last action and write a .ply, so level 1 is its own file first.
$ST "$OUT/sh.ply" -d 50% "$OUT/sh-l1.ply"
$ST "$OUT/sh.ply" -l 0 "$OUT/sh-l1.ply" -l 1 "$OUT/ssog/lod-meta.json" \
    --lod-chunk-extent 1 --lod-chunk-count 1 --lod-chunk-min 0
rm "$OUT/sh-l1.ply"
du -sh "$OUT"
