#!/usr/bin/env python3
"""Fill what the drone never looked at, and write the skybox JPEG.

A drone flies looking forward and down, so the top of the sky is in no frame: R6 covers
to about 50 degrees and nothing above. The hole is a cap around the zenith, and it is
the one part of a sky that is easy to be right about - a clear sky's colour there is a
smooth function of altitude, with no cloud edge to invent.

So the cap is not inpainted from its rim. A low-order spherical harmonic is fitted to
the sky that WAS seen, which is half a hemisphere and plenty to pin a gradient, and the
cap takes the fit. Around the seam the fit is blended into the real pixels over a few
degrees, so the two meet without a line. Pushing the rim upward instead (push-pull, or
any diffusion) smears whatever cloud happened to sit at 50 degrees across the whole top
of the sky, which reads as a streak from every angle the moment the drone rolls.

BELOW the band the fit is not used at all. Fitted to sky and asked about the ground it
runs to -1201, and a clamp only turns that into a sunset-coloured belt around a scene
photographed at seven in the morning. Nothing down there was photographed and the
capture's own ground covers most of it, so the rim simply darkens away: honest, and
quiet enough not to draw the eye to the hole in the middle of it.

usage: sky_fill.py sky.npy out.jpg [--degree 3] [--blend 8] [--quality 92]
"""
import argparse

import numpy as np
from PIL import Image


def sh_basis(x, y, z, degree):
    """Real spherical harmonics up to `degree`, as columns. Same order as everyone's."""
    b = [np.ones_like(x) * 0.282095]
    if degree >= 1:
        b += [0.488603 * y, 0.488603 * z, 0.488603 * x]
    if degree >= 2:
        b += [1.092548 * x * y, 1.092548 * y * z,
              0.315392 * (3 * z * z - 1), 1.092548 * x * z,
              0.546274 * (x * x - y * y)]
    if degree >= 3:
        b += [0.590044 * y * (3 * x * x - y * y), 2.890611 * x * y * z,
              0.457046 * y * (5 * z * z - 1), 0.373176 * z * (5 * z * z - 3),
              0.457046 * x * (5 * z * z - 1), 1.445306 * z * (x * x - y * y),
              0.590044 * x * (x * x - 3 * y * y)]
    return np.stack(b, -1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("npy")
    ap.add_argument("out")
    ap.add_argument("--degree", type=int, default=3)
    ap.add_argument("--blend", type=float, default=8.0, help="seam width, degrees")
    ap.add_argument("--quality", type=int, default=92)
    ap.add_argument("--floor", type=float, default=0.18,
                    help="how dark the unphotographed ground half settles to, times the rim")
    ap.add_argument("--floor-fade", type=float, default=25.0,
                    help="degrees below the band over which it gets there")
    a = ap.parse_args()

    data = np.load(a.npy)
    rgb, cnt = data[..., :3], data[..., 3]
    H, W, _ = rgb.shape
    have = cnt > 0

    lat = np.pi / 2 - (np.arange(H) + 0.5) / H * np.pi
    lon = (np.arange(W) + 0.5) / W * 2 * np.pi - np.pi
    LO, LA = np.meshgrid(lon, lat)
    # The same directions the shader will ask for: u from atan2(x, -z), v off +y.
    x = np.cos(LA) * np.sin(LO)
    y = np.sin(LA)
    z = -np.cos(LA) * np.cos(LO)
    B = sh_basis(x, y, z, a.degree)

    # Fit on what was seen. Weight by how many frames saw it, capped: a bin seen 300
    # times is not 300 times as trustworthy as one seen 20, and without the cap the
    # fit is decided by the few directions the drone stared at.
    w = np.minimum(cnt[have], 30.0)
    A = B[have] * w[:, None]
    coef, *_ = np.linalg.lstsq(A, rgb[have] * w[:, None], rcond=None)
    fit = B @ coef
    resid = rgb[have] - fit[have]
    print(f"SH degree {a.degree} fit over {have.sum()} bins: residual rms "
          f"{np.round(resid.std(0), 1)} of a mean {np.round(rgb[have].mean(0), 0)}")

    # Real pixels win where they exist; the fit takes everything else, cross-faded
    # across the seam so the join is not a line. The hole is a cap above and a cap
    # below, so distance to the nearest seen bin in the same column is the whole
    # geometry of it. Holding the rim's own colour outward instead draws a vertical
    # streak per column, because neighbouring columns end on different pixels.
    seen_rows = [np.nonzero(have[:, c])[0] for c in range(W)]
    rows = np.arange(H)

    def ring_blur(v, k):
        pad = np.concatenate([v[-k:], v, v[:k]])
        ker = np.ones(2 * k + 1) / (2 * k + 1)
        if pad.ndim == 1:
            return np.convolve(pad, ker, "same")[k:-k]
        return np.stack([np.convolve(pad[:, i], ker, "same")[k:-k] for i in range(pad.shape[1])], 1)

    # Where the band ends is a ragged line - one column reaches two degrees higher than
    # its neighbour - and a fade measured from a ragged line is a fade in vertical
    # stripes. The EDGE is smoothed, not just the colour on it.
    top_c = ring_blur(np.array([r[0] if r.size else 0.0 for r in seen_rows]), 31)
    bot_c = ring_blur(np.array([r[-1] if r.size else H - 1.0 for r in seen_rows]), 31)

    d = np.maximum(top_c[None, :] - rows[:, None], 0) + np.maximum(rows[:, None] - bot_c[None, :], 0)
    dist = (d * (180.0 / H)).astype(np.float32)   # degrees outside the seen band
    t = np.clip(dist / max(a.blend, 1e-6), 0, 1)[..., None]

    # Near the seam the fit is nudged onto the real rim so the two agree there exactly:
    # the offset is the rim's own residual, faded out over the same band.
    # A rim read column by column jitters, and an offset that jitters draws vertical
    # lines across the whole cap. Smooth both rims around the horizon before using them.
    def smooth_ring(v, k=31):
        pad = np.concatenate([v[-k:], v, v[:k]])
        ker = np.ones(2 * k + 1) / (2 * k + 1)
        return np.stack([np.convolve(pad[:, i], ker, "same")[k:-k] for i in range(3)], 1)

    ti = np.array([r[0] if r.size else 0 for r in seen_rows])
    bi = np.array([r[-1] if r.size else H - 1 for r in seen_rows])
    top_rim = smooth_ring(np.stack([rgb[ti[c], c] for c in range(W)]))
    bot_rim = smooth_ring(np.stack([rgb[bi[c], c] for c in range(W)]))
    top_fit = np.stack([fit[ti[c], c] for c in range(W)])

    out = np.array(rgb, np.float32)
    up = (~have) & (rows[:, None] < ti[None, :])
    down = (~have) & (rows[:, None] > bi[None, :])

    # Above: the fit, lifted onto the real rim and let go over the seam.
    off = (top_rim - top_fit)[None, :, :]
    out[up] = (fit + off * (1 - t))[up]

    # Below: the rim, darkening. No fit - see the header.
    fade = np.clip(dist / max(a.floor_fade, 1e-6), 0, 1)[..., None]
    k = 1.0 + (a.floor - 1.0) * fade
    out[down] = (bot_rim[None, :, :] * k)[down]

    lo, hi = float(rgb[have].min()), float(rgb[have].max())
    print(f"fill range {out.min():.0f}..{out.max():.0f}; the photographed sky spans {lo:.0f}..{hi:.0f}")

    out = np.clip(out, 0, 255).astype(np.uint8)
    Image.fromarray(out).save(a.out, quality=a.quality, subsampling=0)
    print(f"wrote {a.out}  {W}x{H}  q{a.quality}")


if __name__ == "__main__":
    main()
