#!/usr/bin/env python3
"""Score a sky-masked model on held-out views, over the pixels it was asked to explain.

gsplat's own eval masks the render and then compares it against ground truth that still
contains the sky, so a model trained not to reconstruct the sky is scored against the sky
it deliberately omitted and its PSNR collapses for a reason unrelated to quality. That
makes the built-in number useless for choosing between masked runs.

This scores only the kept pixels - the ground, trees and structures - which is the part
any of these runs is actually trying to get right.

    python3 sky_masked_eval.py <ckpt.pt> [more.pt ...]

Run it on the training box, inside ~/dgs-field/.venv, with the trainer already patched for
masks (the mask has to reach the val split, which the patch does).
"""

import glob
import sys

import numpy as np
import torch

sys.path.insert(0, "/home/saqoosha/gsplat/examples")
from simple_trainer import Config, Runner  # noqa: E402
from lib_bilagrid import color_correct  # noqa: E402

DATA = "/home/saqoosha/dgs-field"


def score(ckpt_path):
    cfg = Config(
        disable_viewer=True,
        data_dir=DATA,
        data_factor=4,
        mask_dir=f"{DATA}/masks",
        result_dir=f"{DATA}/eval/tmp",
    )
    runner = Runner(0, 0, 1, cfg)
    ckpt = torch.load(ckpt_path, map_location=runner.device, weights_only=True)
    for k in runner.splats.keys():
        runner.splats[k].data = ckpt["splats"][k]

    loader = torch.utils.data.DataLoader(runner.valset, batch_size=1, shuffle=False,
                                         num_workers=1)
    psnrs, cc_psnrs, kept_frac = [], [], []
    with torch.no_grad():
        for data in loader:
            pixels = data["image"].to(runner.device) / 255.0
            masks = data["mask"].to(runner.device) if "mask" in data else None
            h, w = pixels.shape[1:3]
            colors, _, _ = runner.rasterize_splats(
                camtoworlds=data["camtoworld"].to(runner.device),
                Ks=data["K"].to(runner.device),
                width=w, height=h,
                sh_degree=cfg.sh_degree,
                near_plane=cfg.near_plane, far_plane=cfg.far_plane,
                masks=masks,
            )
            colors = torch.clamp(colors, 0.0, 1.0)
            if masks is None:
                sel = torch.ones_like(colors, dtype=torch.bool)
                frac = 1.0
            else:
                sel = masks[..., None].expand_as(colors)
                frac = masks.float().mean().item()
            # Mean squared error over kept pixels only, then the usual conversion. Taking
            # it per image and averaging afterwards keeps a frame that is nearly all sky
            # from dominating the total.
            mse = torch.mean((colors[sel] - pixels[sel]) ** 2).item()
            psnrs.append(10.0 * np.log10(1.0 / max(mse, 1e-12)))

            # A bilateral-grid model is only ever rendered through a per-image colour
            # transform during training, and eval has no grid for a held-out view - so it
            # comes out in a canonical colour space and raw PSNR charges it for an offset
            # rather than for anything structural. Fitting an affine colour map per image
            # first is how upstream compares those runs, and it is the only reading that
            # puts every variant on the same footing.
            cc = color_correct(colors, pixels)
            cc_mse = torch.mean((cc[sel] - pixels[sel]) ** 2).item()
            cc_psnrs.append(10.0 * np.log10(1.0 / max(cc_mse, 1e-12)))
            kept_frac.append(frac)
    return (float(np.mean(psnrs)), float(np.mean(cc_psnrs)),
            float(np.mean(kept_frac)), len(psnrs))


for path in sys.argv[1:]:
    name = path.split("/")[-3] if "/" in path else path
    p, cc, frac, n = score(path)
    print(f"RESULT {name:28s} masked_PSNR {p:6.3f}  colour-corrected {cc:6.3f}"
          f"  kept {frac*100:5.1f}%  views {n}")
