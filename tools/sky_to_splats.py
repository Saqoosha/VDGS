#!/usr/bin/env python3
"""Bake a sky panorama into a capture as a dome of splats.

For viewers that take a splat file and nothing else - SuperSplat's Publish carries the
splats and a background colour, not a skybox - the sky has to be splats itself. A
dome of flat, opaque discs at a large radius, each facing the centre and coloured
from the panorama in its direction, is that sky: drawn behind everything, sorted like
any other splat, and inside the viewer's far clip because it is part of the bounds
the far clip is fitted to.

The discs sit on a golden-spiral lattice - equal area everywhere, so neither the
horizon nor the zenith gets a crowd - down to a little below the horizon, where the
capture's own ground takes over. Their Gaussian width is a fraction of the lattice
spacing large enough that three overlapping neighbours leave no see-through gap
(0.8 of the spacing: about 99% coverage between centres). That width is the sky's
effective blur; the default spacing suits a panorama already blurred by a few pixels.

The panorama must be in the frame of the .ply it is baked into, with the equirect
convention of tools/sky_pano.py: row 0 is the zenith, u = atan2(x, -z) / 2pi + 0.5.

    python3 tools/sky_to_splats.py scene.ply sky.jpg out.ply [--radius 2000]
        [--spacing 0.3] [--width 0.8] [--below -8] [--center x,y,z]
"""
import argparse
import math

import numpy as np
from PIL import Image

C0 = 0.28209479177387814


def read_ply(path):
    with open(path, "rb") as f:
        header = b""
        while not header.endswith(b"end_header\n"):
            c = f.read(1)
            if not c:
                raise SystemExit(f"{path}: not a binary ply")
            header += c
        text = header.decode("ascii")
        if "binary_little_endian" not in text:
            raise SystemExit(f"{path}: only binary_little_endian is supported")
        props, n = [], 0
        for line in text.splitlines():
            p = line.split()
            if p[:2] == ["element", "vertex"]:
                n = int(p[2])
            elif p and p[0] == "property":
                if p[1] != "float":
                    raise SystemExit(f"{path}: property {p[2]} is {p[1]}, only float is supported")
                props.append(p[2])
        data = np.fromfile(f, dtype=np.float32, count=n * len(props)).reshape(n, len(props))
    return text, props, data


def fibonacci_dirs(n_full, below_deg):
    """Golden-spiral directions over the whole sphere, kept where altitude >= below."""
    i = np.arange(n_full) + 0.5
    y = 1.0 - 2.0 * i / n_full                      # evenly spaced in height = equal area
    r = np.sqrt(np.maximum(0.0, 1.0 - y * y))
    phi = i * math.pi * (3.0 - math.sqrt(5.0))
    d = np.stack([r * np.cos(phi), y, r * np.sin(phi)], 1)
    return d[y >= math.sin(math.radians(below_deg))]


def quat_z_to(d):
    """Unit quaternions (w, x, y, z) turning +z onto each row of d."""
    z = np.array([0.0, 0.0, 1.0])
    axis = np.cross(np.broadcast_to(z, d.shape), d)
    s = np.linalg.norm(axis, axis=1)
    c = d[:, 2]
    angle = np.arctan2(s, c)
    axis = np.where(s[:, None] > 1e-9, axis / np.maximum(s, 1e-12)[:, None], np.array([1.0, 0, 0]))
    h = angle / 2
    return np.concatenate([np.cos(h)[:, None], axis * np.sin(h)[:, None]], 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ply")
    ap.add_argument("sky")
    ap.add_argument("out")
    ap.add_argument("--radius", type=float, default=2000.0, help="dome radius, scene units")
    ap.add_argument("--spacing", type=float, default=0.3, help="angle between discs, degrees")
    ap.add_argument("--below", type=float, default=-8.0, help="lowest altitude covered, degrees")
    ap.add_argument("--width", type=float, default=0.8, help="Gaussian sigma, times the spacing")
    ap.add_argument("--center", help="x,y,z of the dome; default the opaque splats' median")
    a = ap.parse_args()

    header, props, data = read_ply(a.ply)
    col = {k: i for i, k in enumerate(props)}
    need = ["x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
            "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]
    missing = [k for k in need if k not in col]
    if missing:
        raise SystemExit(f"{a.ply}: missing {missing}")

    if a.center:
        center = np.array([float(v) for v in a.center.split(",")])
    else:
        op = 1 / (1 + np.exp(-data[:, col["opacity"]]))
        center = np.median(data[op > 0.3][:, [col["x"], col["y"], col["z"]]], axis=0)

    sky = np.asarray(Image.open(a.sky).convert("RGB")).astype(np.float32) / 255.0
    H, W, _ = sky.shape

    step = math.radians(a.spacing)
    n_full = int(round(4 * math.pi / (step * step * math.sqrt(3) / 2)))   # hexagonal cell area
    d = fibonacci_dirs(n_full, a.below)

    # Colour from the panorama, bilinear, wrapping in u.
    u = np.arctan2(d[:, 0], -d[:, 2]) / (2 * math.pi) + 0.5
    v = (math.pi / 2 - np.arcsin(np.clip(d[:, 1], -1, 1))) / math.pi
    x = u * W - 0.5
    y = np.clip(v * H - 0.5, 0, H - 1)
    x0 = np.floor(x).astype(int)
    y0 = np.floor(y).astype(int)
    fx, fy = (x - x0)[:, None], (y - y0)[:, None]
    x1, y1 = (x0 + 1) % W, np.minimum(y0 + 1, H - 1)
    x0 %= W
    rgb = (sky[y0, x0] * (1 - fx) * (1 - fy) + sky[y0, x1] * fx * (1 - fy)
           + sky[y1, x0] * (1 - fx) * fy + sky[y1, x1] * fx * fy)

    sigma = a.width * step * a.radius          # scene units on the dome
    out = np.zeros((len(d), len(props)), np.float32)
    pos = center + d * a.radius
    out[:, [col["x"], col["y"], col["z"]]] = pos
    out[:, [col["f_dc_0"], col["f_dc_1"], col["f_dc_2"]]] = (rgb - 0.5) / C0
    out[:, col["opacity"]] = math.log(0.995 / 0.005)
    # Flat along local z, which quat_z_to aligns with the radius.
    out[:, col["scale_0"]] = math.log(sigma)
    out[:, col["scale_1"]] = math.log(sigma)
    out[:, col["scale_2"]] = math.log(sigma * 0.02)
    q = quat_z_to(d)
    for i in range(4):
        out[:, col[f"rot_{i}"]] = q[:, i]
    # f_rest (view-dependent colour) and normals stay zero: the sky looks the same from
    # every place the camera can be.

    n0 = len(data)
    merged = np.concatenate([data, out])
    new_header = header.replace(f"element vertex {n0}", f"element vertex {len(merged)}")
    with open(a.out, "wb") as f:
        f.write(new_header.encode("ascii"))
        merged.astype(np.float32).tofile(f)
    print(f"dome: {len(d)} splats, radius {a.radius:g}, spacing {a.spacing}deg, sigma {sigma:.2f}, "
          f"down to {a.below}deg, centre {np.round(center, 2)}")
    print(f"wrote {a.out}: {n0} + {len(d)} = {len(merged)} splats")


if __name__ == "__main__":
    main()
