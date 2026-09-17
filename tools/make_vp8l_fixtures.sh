#!/usr/bin/env bash
# Lossless WebP fixtures for Vp8lDecoderTests, each paired with dwebp's own decode.
#
#   bash tools/make_vp8l_fixtures.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/src/VDGS.Tests/fixtures/vp8l"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
mkdir -p "$OUT"
python3 - "$TMP" <<'EOF'
import sys, random, struct, zlib
out = sys.argv[1]
def png(path, w, h, px):   # px: list of (r,g,b,a)
    raw = b''.join(b'\x00' + b''.join(bytes(p) for p in px[y*w:(y+1)*w]) for y in range(h))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
                           + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b''))
r = random.Random(7)
png(f'{out}/noise.png', 97, 61, [(r.randrange(256), r.randrange(256), r.randrange(256), r.randrange(256)) for _ in range(97*61)])
png(f'{out}/gradient.png', 256, 128, [(x, y*2, (x+y) & 255, 255) for y in range(128) for x in range(256)])
for n in (2, 4, 16, 200):
    pal = [(r.randrange(256), r.randrange(256), r.randrange(256), 255) for _ in range(n)]
    png(f'{out}/palette{n}.png', 64, 64, [pal[r.randrange(n)] for _ in range(64*64)])
png(f'{out}/repeat.png', 300, 300, [((x // 7) * 13 & 255, (y // 5) * 17 & 255, 40, 255) for y in range(300) for x in range(300)])
png(f'{out}/one.png', 1, 1, [(1, 2, 3, 4)])
# Large tiled pattern: cwebp turns this into color-cache hits and long LZ77 distances.
# (The smaller fixtures above often stay in short-distance / no-cache territory.)
tile = 32
png(f'{out}/tile1024.png', 1024, 1024, [
    ((x % tile) * 8 & 255, (y % tile) * 8 & 255, ((x // tile) * 17 + (y // tile) * 31) & 255, 255)
    for y in range(1024) for x in range(1024)
])
EOF
for png in "$TMP"/*.png; do
  base="$(basename "$png" .png)"
  for z in 0 6 9; do
    cwebp -quiet -lossless -exact -z "$z" "$png" -o "$OUT/$base-z$z.webp"
    dwebp -quiet "$OUT/$base-z$z.webp" -pam -o "$TMP/x.pam"
    # PAM header is 7 text lines; the body is RGBA8 row-major. Only its size and hash are
    # kept - the raw pixels of the 1024x1024 case alone are 4 MB a copy.
    tail -c +$(( $(head -n 7 "$TMP/x.pam" | wc -c) + 1 )) "$TMP/x.pam" > "$TMP/x.rgba"
    printf '%s %s\n' "$(wc -c < "$TMP/x.rgba" | tr -d ' ')" \
      "$(shasum -a 256 "$TMP/x.rgba" | cut -d' ' -f1)" > "$OUT/$base-z$z.sha256"
  done
done
ls "$OUT" | wc -l
