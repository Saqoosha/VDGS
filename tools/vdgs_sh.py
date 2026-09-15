#!/usr/bin/env python3
"""
Transform the spherical harmonics of an already-converted VDGS capture in place.

Captures deployed before 2026-09-16 were converted from a .ply whose geometry had been
mirrored or rotated with its SH left alone (tools/splat_sh.py). When the source .ply is
not available any more, this rewrites sh.bin and chunk.bin directly; every other file is
copied unchanged, except that --revision N rewrites meta.json's revision.

Only the High quality layout is handled: SH as Norm11 (11.10.11 bits per coefficient,
chunk-relative) with per-chunk f16 bounds in ChunkInfo (64 bytes, see ARCHITECTURE).

    python3 tools/vdgs_sh.py --matrix build/fit/FDF-matrix.json --revision 2 in-dir out-dir
    python3 tools/vdgs_sh.py --mirror y in-dir out-dir
    python3 tools/vdgs_sh.py --check-frame final.ply in-dir      # is in-dir a subset of final.ply? (needs scipy)
    python3 tools/vdgs_sh.py --roundtrip in-dir                  # decode/encode only; exits 1 on drift
"""
import argparse
import json
import os
import shutil
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import splat_sh  # noqa: E402

CHUNK = 256
CHUNK_DTYPE = np.dtype([("col", "<u4", 4), ("pos", "<f4", 6), ("scl", "<u4", 3), ("sh", "<u4", 3)])
assert CHUNK_DTYPE.itemsize == 64


def f16_lo(u):  # low 16 bits as float
    return (u & 0xFFFF).astype(np.uint16).view(np.float16).astype(np.float32)


def f16_hi(u):
    return (u >> 16).astype(np.uint16).view(np.float16).astype(np.float32)


def pack_f16_pair(lo, hi):
    return (lo.astype(np.float16).view(np.uint16).astype(np.uint32)
            | (hi.astype(np.float16).view(np.uint16).astype(np.uint32) << 16))


def load(d):
    meta = json.load(open(os.path.join(d, "meta.json")))
    if meta.get("shFormat") != "Norm11":
        sys.exit(f"{d}: shFormat is {meta.get('shFormat')}, only Norm11 (quality High) is handled")
    n, nc = meta["splatCount"], meta["chunkCount"]
    chunks = np.fromfile(os.path.join(d, "chunk.bin"), dtype=CHUNK_DTYPE)
    sh = np.fromfile(os.path.join(d, "sh.bin"), dtype="<u4").reshape(n, 15)
    if len(chunks) != nc or nc != (n + CHUNK - 1) // CHUNK:
        sys.exit(f"{d}: chunk count {len(chunks)} does not match {n} splats")
    return meta, chunks, sh


def decode(chunks, sh):
    """(n, 15, 3) float32 SH coefficients, dequantised (absolute, not chunk-relative)."""
    n = sh.shape[0]
    ci = np.arange(n) // CHUNK
    lo = np.stack([f16_lo(chunks["sh"][:, c]) for c in range(3)], axis=1)[ci]   # (n, 3)
    hi = np.stack([f16_hi(chunks["sh"][:, c]) for c in range(3)], axis=1)[ci]
    norm = np.stack([(sh & 2047) / 2047.0, ((sh >> 11) & 1023) / 1023.0, ((sh >> 21) & 2047) / 2047.0],
                    axis=2).astype(np.float32)                                    # (n, 15, 3)
    return lo[:, None, :] + norm * (hi - lo)[:, None, :]


def encode(coeffs, chunks):
    """New chunk SH bounds and Norm11 words for (n, 15, 3) coefficients."""
    n = coeffs.shape[0]
    nc = len(chunks)
    out_chunks = chunks.copy()
    words = np.empty((n, 15), dtype=np.uint32)
    for c in range(nc):
        blk = coeffs[c * CHUNK:(c + 1) * CHUNK]                     # (m, 15, 3)
        lo = blk.min(axis=(0, 1)).astype(np.float16).astype(np.float32)   # bounds as the f16 the shader will read
        hi = blk.max(axis=(0, 1)).astype(np.float16).astype(np.float32)
        span = hi - lo
        span[span == 0] = 1.0
        norm = np.clip((blk - lo) / span, 0.0, 1.0)
        # Same truncating quantiser as upstream GaussianSplatAssetCreator.EncodeFloat3ToNorm11.
        x = np.minimum((norm[:, :, 0] * 2047.5).astype(np.uint32), 2047)
        y = np.minimum((norm[:, :, 1] * 1023.5).astype(np.uint32), 1023)
        z = np.minimum((norm[:, :, 2] * 2047.5).astype(np.uint32), 2047)
        words[c * CHUNK:(c + 1) * CHUNK] = x | (y << 11) | (z << 21)
        out_chunks["sh"][c] = pack_f16_pair(lo, hi)
    return out_chunks, words


def positions(meta, chunks, d):
    if meta.get("posFormat") != "Norm16":
        sys.exit(f"posFormat {meta.get('posFormat')}: --check-frame handles Norm16 only")
    n = meta["splatCount"]
    raw = np.fromfile(os.path.join(d, "pos.bin"), dtype="<u2")[: n * 3].reshape(n, 3).astype(np.float32) / 65535.0  # file is padded past n*6 bytes
    ci = np.arange(n) // CHUNK
    p = chunks["pos"][ci].reshape(n, 3, 2)
    return p[:, :, 0] + raw * (p[:, :, 1] - p[:, :, 0])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--matrix", help="fit_transform.py output; its R (rotation/reflection) is applied")
    ap.add_argument("--mirror", choices=list(splat_sh.MIRROR))
    ap.add_argument("--check-frame", metavar="FINAL.ply", help="verify in-dir positions lie on FINAL.ply's")
    ap.add_argument("--roundtrip", action="store_true")
    ap.add_argument("--revision", type=int, help="stamp meta.json in out_dir with this revision (the companion offers an update when the installed one is lower)")
    ap.add_argument("in_dir")
    ap.add_argument("out_dir", nargs="?")
    args = ap.parse_args()

    meta, chunks, sh = load(args.in_dir)
    n = meta["splatCount"]

    if args.check_frame:
        from scipy.spatial import cKDTree
        _, props, data = splat_sh._read_ply(args.check_frame)
        ref = data[:, [props.index("x"), props.index("y"), props.index("z")]].astype(np.float64)
        p = positions(meta, chunks, args.in_dir).astype(np.float64)
        rng = np.random.default_rng(0)
        take = rng.choice(n, min(50000, n), replace=False)
        d, _ = cKDTree(ref).query(p[take], workers=-1)
        print(f"{n} splats vs {len(ref)} in {os.path.basename(args.check_frame)}: "
              f"median nn {np.median(d):.5f} m, p99 {np.percentile(d, 99):.5f} m, "
              f"within 1 cm {100 * (d < 0.01).mean():.2f}%")
        return

    coeffs = decode(chunks, sh)
    if args.roundtrip:
        c2, w2 = encode(coeffs, chunks)
        back = decode(c2, w2)
        changed = (w2 != sh).mean()
        drift = np.abs(back - coeffs).max()
        quantum = np.median((f16_hi(chunks['sh'][:, 0]) - f16_lo(chunks['sh'][:, 0])) / 2047)
        print(f"roundtrip: words changed {100 * changed:.3f}%  max |coeff drift| {drift:.5f}  (quantum {quantum:.5f})")
        ok = changed < 0.001 and drift <= 2 * quantum
        print("OK" if ok else "MISMATCH: encoder does not reproduce the converter's words")
        sys.exit(0 if ok else 1)

    if not args.out_dir or not (args.matrix or args.mirror):
        ap.error("need --matrix or --mirror, plus out_dir")
    R = np.asarray(json.load(open(args.matrix))["R"]) if args.matrix else splat_sh.MIRROR[args.mirror]
    D = splat_sh.transform_matrix(R)
    for c in range(3):
        coeffs[:, :, c] = (coeffs[:, :, c] @ D.T).astype(np.float32)
    new_chunks, new_sh = encode(coeffs, chunks)

    os.makedirs(args.out_dir, exist_ok=True)
    for f in os.listdir(args.in_dir):
        if f not in ("sh.bin", "chunk.bin"):
            shutil.copy2(os.path.join(args.in_dir, f), os.path.join(args.out_dir, f))
    new_chunks.tofile(os.path.join(args.out_dir, "chunk.bin"))
    new_sh.tofile(os.path.join(args.out_dir, "sh.bin"))
    if args.revision is not None:
        meta["revision"] = args.revision
        with open(os.path.join(args.out_dir, "meta.json"), "w") as f:
            json.dump(meta, f, indent=2)
            f.write("\n")
    print(f"wrote {args.out_dir}: {n} splats, SH transformed, everything else copied")
    print("R =\n" + np.array2string(R, precision=6, suppress_small=True))


if __name__ == "__main__":
    main()
