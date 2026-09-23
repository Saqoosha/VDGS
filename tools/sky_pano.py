#!/usr/bin/env python3
"""Build an equirectangular sky panorama from the footage and the SfM poses.

The sky in a capture is infinitely far, so every frame that sees a patch of it sees
the same patch — the camera's position does not matter, only its orientation. So each
sky pixel can be turned into a world direction and dropped into an equirect bin, and
the frames stack into one panorama.

Exposure drifts between frames (the trainer's PPISP absorbed some of it), so each bin
takes the MEDIAN of what landed in it rather than the mean: a frame that came in a stop
brighter moves the median by nothing once a few frames overlap.

What the first drafts got wrong, all of it visible as salt-and-pepper across the band:

- A bin's samples have to come from DIFFERENT MOMENTS of the flight. Keeping the first
  sixteen that land fills it from consecutive frames, and a branch that sat in the mask
  for half a second is then in most of them - the median defends nothing. Reservoir
  sampling spreads the sixteen over every frame that saw the bin.
- The mask's own edge is not sky, and eroding it is not enough. SAM draws one smooth
  outline around a tree; a tree's real edge is leaves, and the ones that stick up past
  the outline end up inside the sky. Twelve pixels of erosion does not reach them and a
  hundred would eat the horizon. So samples are also cut by BRIGHTNESS, against the
  frame's own masked-sky median rather than an absolute number - exposure drifts through
  the flight, but a leaf is a fifth of the sky's brightness in any exposure.
- ONE sample per bin per frame. A 2688-wide frame covers a bin with dozens of its own
  pixels, and fancy indexing writes one of them while the counter counts them all: the
  bin then claims sixteen samples holding three, and the median is over thirteen zeros.
  That is what the first three drafts drew - not leaves, not exposure, black. The frames
  feeding a bin that came out (0,0,0) were all looking at clean sky.
- That cut is worthless on a frame whose mask is wrong WHOLESALE. A handful of frames
  come back with a quarter of the picture called sky at a median brightness of 73 where
  the flight's is 190: the segmenter lost the sky and handed back shade. Judged against
  its own median such a frame rejects nothing, and its whole field of view lands in the
  panorama as one dark quadrilateral - which is exactly what the first two drafts drew.
  So a first pass measures every frame, and a frame whose sky is not bright against the
  FLIGHT's median is dropped entirely.

Directions come out in the FRAME OF THE PLY, not the SfM's own: --frame takes the same
matrix json that built the .ply from the model, so the panorama and the splats share one
frame. The consumer then needs only the capture object's worldToLocal, which already
carries up, turn and anything a later tweak does to them. Leaving the sky in the SfM
frame instead means re-deriving that transform by hand, and every sign of it is a chance
to put the sun on the wrong side of the scene.

usage: sky_pano.py MODEL_DIR IMAGE_DIR MASK_DIR OUT.npy --frame enu_to_unity.json
"""
import argparse
import os
import struct

import numpy as np
from PIL import Image


def read_cameras_bin(p):
    out = {}
    with open(p, 'rb') as f:
        n = struct.unpack('<Q', f.read(8))[0]
        for _ in range(n):
            cid, model, w, h = struct.unpack('<iiQQ', f.read(24))
            # param counts by COLMAP model id
            npar = {0: 3, 1: 4, 2: 4, 3: 5, 4: 8, 5: 12, 6: 8, 7: 5, 8: 4, 9: 5, 10: 8, 11: 12}[model]
            par = struct.unpack('<' + 'd' * npar, f.read(8 * npar))
            out[cid] = (model, w, h, par)
    return out


def read_images_bin(p):
    out = {}
    with open(p, 'rb') as f:
        n = struct.unpack('<Q', f.read(8))[0]
        for _ in range(n):
            iid, qw, qx, qy, qz, tx, ty, tz, cam = struct.unpack('<idddddddi', f.read(64))
            name = b''
            while True:
                ch = f.read(1)
                if ch == b'\x00':
                    break
                name += ch
            npts = struct.unpack('<Q', f.read(8))[0]
            f.read(24 * npts)
            out[name.decode()] = ((qw, qx, qy, qz), (tx, ty, tz), cam)
    return out


def rot_from_quat(q):
    qw, qx, qy, qz = q
    return np.array([
        [1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)],
        [2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)],
        [2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)],
    ])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model_dir")
    ap.add_argument("image_dir")
    ap.add_argument("mask_dir")
    ap.add_argument("out")
    ap.add_argument("--width", type=int, default=2048)
    ap.add_argument("--step", type=int, default=4, help="sample every Nth source pixel")
    ap.add_argument("--max-frames", type=int, default=0)
    ap.add_argument("--min-alt", type=float, default=3.0,
                    help="degrees above horizon a direction must clear to count as sky")
    ap.add_argument("--frame", help="matrix json (scale/R/t) taking the model frame to the ply's")
    ap.add_argument("--erode", type=int, default=3,
                    help="shrink the sky mask by this many sampled pixels before reading it")
    ap.add_argument("--dark", type=float, default=0.55,
                    help="drop samples dimmer than this times the frame's own masked-sky median")
    ap.add_argument("--frame-dark", type=float, default=0.65,
                    help="drop a frame whose masked-sky median is below this times the flight's")
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()

    rng = np.random.default_rng(a.seed)
    F = np.eye(3)
    if a.frame:
        import json
        F = np.array(json.load(open(a.frame))["R"], float)

    cams = read_cameras_bin(os.path.join(a.model_dir, "cameras.bin"))
    imgs = read_images_bin(os.path.join(a.model_dir, "images.bin"))
    W, H = a.width, a.width // 2
    # per-bin sample lists are too big to keep; two passes would cost a re-decode, so
    # keep a fixed reservoir per bin instead: 16 samples is plenty for a median.
    K = 16
    res = np.zeros((H, W, K, 3), np.uint8)
    cnt = np.zeros((H, W), np.int32)      # kept samples, capped at K
    seen = np.zeros((H, W), np.int32)     # samples offered, uncapped: the reservoir's n

    names = sorted(imgs)
    if a.max_frames:
        names = names[:: max(1, len(names) // a.max_frames)]

    # Pass one: how bright is each frame's sky? Read coarsely - a median does not need
    # every pixel, and this pass exists only to find the frames whose mask failed.
    medians = {}
    for name in names:
        ipath = os.path.join(a.image_dir, name)
        mpath = os.path.join(a.mask_dir, os.path.splitext(name)[0] + ".png")
        if not (os.path.exists(ipath) and os.path.exists(mpath)):
            continue
        im = np.asarray(Image.open(ipath).convert("L"))[::16, ::16]
        mk = np.asarray(Image.open(mpath).convert("L").resize((im.shape[1], im.shape[0])))
        sel = mk > 127
        if sel.sum() >= 16:
            medians[name] = float(np.median(im[sel]))
    flight = float(np.median(list(medians.values()))) if medians else 0.0
    floor = a.frame_dark * flight
    dropped = [n for n, m in medians.items() if m < floor]
    print(f"flight sky median {flight:.0f}; dropping {len(dropped)} frame(s) whose sky is under "
          f"{floor:.0f}", flush=True)

    used = 0
    for i, name in enumerate(names):
        ipath = os.path.join(a.image_dir, name)
        mpath = os.path.join(a.mask_dir, os.path.splitext(name)[0] + ".png")
        if not (os.path.exists(ipath) and os.path.exists(mpath)):
            continue
        if medians.get(name, 0.0) < floor:
            continue
        q, t, cid = imgs[name]
        model, cw, ch, par = cams[cid]
        im = np.asarray(Image.open(ipath).convert("RGB"))
        mk = np.asarray(Image.open(mpath).convert("L"))
        if mk.shape[:2] != im.shape[:2]:
            mk = np.asarray(Image.open(mpath).convert("L").resize((im.shape[1], im.shape[0])))
        h, w = im.shape[:2]
        sx, sy = w / cw, h / ch
        if model in (1, 4):      # PINHOLE, OPENCV
            fx, fy, cx, cy = par[0] * sx, par[1] * sy, par[2] * sx, par[3] * sy
        else:                     # SIMPLE_PINHOLE / SIMPLE_RADIAL
            fx = fy = par[0] * sx
            cx, cy = par[1] * sx, par[2] * sy

        sub = mk[:: a.step, :: a.step] > 127
        for _ in range(a.erode):          # 4-neighbour erosion, one ring per pass
            e = sub.copy()
            e[1:] &= sub[:-1]; e[:-1] &= sub[1:]
            e[:, 1:] &= sub[:, :-1]; e[:, :-1] &= sub[:, 1:]
            e[0] = e[-1] = False; e[:, 0] = e[:, -1] = False
            sub = e
        ys, xs = np.nonzero(sub)
        if ys.size == 0:
            continue
        ys, xs = ys * a.step, xs * a.step
        # camera ray (COLMAP: +x right, +y down, +z forward), distortion ignored: the
        # sky is smooth and k1 is -0.002, so the error is under a pixel.
        d = np.stack([(xs - cx) / fx, (ys - cy) / fy, np.ones_like(xs, float)], 1)
        d /= np.linalg.norm(d, axis=1, keepdims=True)
        R = rot_from_quat(q)          # world -> camera
        dw = (d @ R) @ F.T            # camera -> model world -> the ply's frame

        # In the ply's frame up is +y (Unity's convention); in the model's it was +z.
        alt = np.arcsin(np.clip(dw[:, 1], -1, 1))
        keep = alt > np.radians(a.min_alt)
        if not keep.any():
            continue
        dw, ys, xs = dw[keep], ys[keep], xs[keep]
        # Equirectangular, matching PanoSkybox.shader: u from atan2(x, -z), v from the
        # angle off +y. Both sides written from the same two lines so they cannot drift.
        lon = np.arctan2(dw[:, 0], -dw[:, 2])
        lat = np.arcsin(np.clip(dw[:, 1], -1, 1))
        px = ((lon + np.pi) / (2 * np.pi) * W).astype(np.int32) % W
        py = np.clip(((np.pi / 2 - lat) / np.pi * H).astype(np.int32), 0, H - 1)
        col = im[ys, xs]

        # ... and the leaves the outline missed, by how dark they are (see the header).
        if a.dark > 0:
            lum = col.astype(np.float32).mean(1)
            bright = lum > a.dark * np.median(lum)
            if not bright.any():
                continue
            py, px, col = py[bright], px[bright], col[bright]

        # One sample per bin from this frame: neighbouring source pixels land in the
        # same bin, and every one of them would otherwise be counted while only the last
        # is stored. np.unique also makes "how many frames saw this" the honest number.
        flat = py.astype(np.int64) * W + px
        _, first = np.unique(flat, return_index=True)
        py, px, col = py[first], px[first], col[first]

        # Reservoir sampling, per bin: an empty slot takes the sample, a full bin keeps
        # it with probability K/n so every frame that saw this patch has the same say.
        # Two samples from one frame landing in one bin race; either is as good.
        n_before = seen[py, px]
        np.add.at(seen, (py, px), 1)
        slot = cnt[py, px]
        room = slot < K
        res[py[room], px[room], slot[room]] = col[room]
        np.add.at(cnt, (py[room], px[room]), 1)

        full = ~room
        if full.any():
            idx = np.nonzero(full)[0]
            keep = rng.random(idx.size) < (K / np.maximum(n_before[idx] + 1, K))
            idx = idx[keep]
            if idx.size:
                res[py[idx], px[idx], rng.integers(0, K, idx.size)] = col[idx]
        used += 1
        if used % 50 == 0:
            print(f"  {used}/{len(names)} frames, coverage {(cnt > 0).mean() * 100:.1f}%", flush=True)

    out = np.zeros((H, W, 3), np.float32)
    have = cnt > 0
    for k in range(1, K + 1):
        m = have & (cnt == k)
        if m.any():
            out[m] = np.median(res[m][:, :k, :].astype(np.float32), axis=1)
    np.save(a.out, np.concatenate([out, seen[..., None].astype(np.float32)], axis=2))
    print(f"frames used {used}; sky coverage {(cnt > 0).mean() * 100:.1f}% of the whole sphere, "
          f"{(cnt[:H // 2] > 0).mean() * 100:.1f}% of the upper half -> {a.out}")


if __name__ == "__main__":
    main()
