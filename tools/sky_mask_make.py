#!/usr/bin/env python3
"""Write a sky mask per training image, so the trainer never has to explain the sky.

An outdoor capture spends most of every frame on sky, and 3DGS answers that by parking
large near-opaque gaussians above the scene: measured on our own field capture, the 1,694
splats over 2 m across and above 15 m average RGB 0.79/0.82/0.85 at opacity 0.77, while
the treeline is 10 m. Those are the clouds. Deleting them afterwards is a threshold fight
- raise it and treetops and power lines go too, lower it and residue stays - so remove the
reason they are created instead. VelociDrone draws its own sky, so a capture needs none.

Masks follow COLMAP's convention: sky is BLACK, everything to keep is WHITE. Most sky
segmenters emit the opposite, and this script inverts for you.

    python3 sky_mask_make.py --data-dir ~/dgs-field            # all images
    python3 sky_mask_make.py --data-dir ~/dgs-field --limit 5  # spot-check first

The model (SegFormer, ADE20K, class 2 = sky) is NVIDIA-licensed for research and
evaluation only. Nothing here redistributes it - the weights are fetched at run time and
the masks stay local.
"""

import argparse
import os
import sys

import numpy as np
from PIL import Image

# ADE20K's label order is shared by every checkpoint family (SegFormer, Mask2Former,
# OneFormer, UPerNet), so this index is safe - but it is asserted at startup anyway.
SKY_CLASS = 2


def parse_args():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data-dir", required=True,
                   help="COLMAP dataset root (holds images_png_df2 / images_png_df4)")
    p.add_argument("--src-subdir", default="images_png_df2",
                   help="images to segment - a larger one recalls thin structures better")
    p.add_argument("--out-subdir", default="masks",
                   help="written next to the images, one PNG per image")
    p.add_argument("--match-subdir", default="images_png_df4",
                   help="masks are written at this directory's resolution")
    p.add_argument("--model", default="nvidia/segformer-b4-finetuned-ade-512-512")
    p.add_argument("--infer-long-side", type=int, default=1536,
                   help="longest side fed to the model; larger keeps thinner structures")
    p.add_argument("--threshold", type=float, default=0.5)
    p.add_argument("--shrink-sky", type=int, default=4,
                   help="pixels of sky given back at the boundary. Erodes the SKY, never "
                        "the subject: a mask that eats a power line costs a real feature, "
                        "a mask that leaves a rim of sky costs a few stray gaussians.")
    p.add_argument("--limit", type=int, default=0, help="only N images")
    p.add_argument("--stride", type=int, default=0,
                   help="with --limit, spread the sample across the whole set instead of "
                        "taking the first N. A flight starts with nadir frames that hold "
                        "no sky at all, so the first N say nothing about the capture.")
    p.add_argument("--overwrite", action="store_true")
    return p.parse_args()


def main():
    args = parse_args()
    import cv2
    import torch
    from transformers import SegformerForSemanticSegmentation, SegformerImageProcessor

    src_dir = os.path.join(args.data_dir, args.src_subdir)
    match_dir = os.path.join(args.data_dir, args.match_subdir)
    out_dir = os.path.join(args.data_dir, args.out_subdir)
    for d in (src_dir, match_dir):
        if not os.path.isdir(d):
            sys.exit(f"missing directory: {d}")
    os.makedirs(out_dir, exist_ok=True)

    names = sorted(n for n in os.listdir(src_dir)
                   if n.lower().endswith((".png", ".jpg", ".jpeg")))
    if args.limit:
        if args.stride or len(names) > args.limit * 4:
            step = args.stride or max(1, len(names) // args.limit)
            names = names[::step][: args.limit]
        else:
            names = names[: args.limit]
    if not names:
        sys.exit(f"no images under {src_dir}")

    # Masks have to line up with the images the trainer actually reads, and the loader
    # remaps and crops them together - a mask authored at a different size drifts by a
    # pixel or two at every silhouette, which is exactly where it matters.
    with Image.open(os.path.join(match_dir, sorted(os.listdir(match_dir))[0])) as im:
        out_w, out_h = im.size

    device = "cuda" if torch.cuda.is_available() else "cpu"
    processor = SegformerImageProcessor.from_pretrained(args.model)
    model = SegformerForSemanticSegmentation.from_pretrained(args.model).to(device).eval()
    label = model.config.id2label[SKY_CLASS]
    assert label.lower().startswith("sky"), f"class {SKY_CLASS} is '{label}', not sky"
    print(f"{args.model} on {device}: class {SKY_CLASS} = '{label}'")
    print(f"{len(names)} images -> {out_dir} at {out_w}x{out_h}")

    kernel = None
    if args.shrink_sky > 0:
        k = 2 * args.shrink_sky + 1
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))

    stats = []
    for i, name in enumerate(names):
        out_path = os.path.join(out_dir, os.path.splitext(name)[0] + ".png")
        if os.path.exists(out_path) and not args.overwrite:
            continue

        img = Image.open(os.path.join(src_dir, name)).convert("RGB")
        scale = args.infer_long_side / max(img.size)
        if scale < 1.0:
            img_in = img.resize((round(img.width * scale), round(img.height * scale)),
                                Image.LANCZOS)
        else:
            img_in = img

        inputs = processor(images=img_in, return_tensors="pt").to(device)
        with torch.no_grad():
            logits = model(**inputs).logits          # [1, C, h/4, w/4]
        prob = torch.softmax(logits, dim=1)[0, SKY_CLASS]

        # Resample the probability, threshold last. Thresholding before the resize lets
        # nearest-neighbour aliasing swallow one-pixel-wide structures.
        sky_prob = cv2.resize(prob.float().cpu().numpy(), (out_w, out_h),
                              interpolation=cv2.INTER_LINEAR)
        sky = (sky_prob > args.threshold).astype(np.uint8)
        if kernel is not None:
            sky = cv2.erode(sky, kernel, iterations=1)

        keep = (1 - sky) * 255
        Image.fromarray(keep.astype(np.uint8), mode="L").save(out_path, optimize=True)

        frac = float(sky.mean())
        stats.append(frac)
        if i < 3 or (i + 1) % 50 == 0 or i == len(names) - 1:
            print(f"  [{i + 1}/{len(names)}] {name}  sky {frac * 100:5.1f}%")

    if stats:
        a = np.asarray(stats)
        print(f"\nsky coverage: mean {a.mean() * 100:.1f}%  "
              f"min {a.min() * 100:.1f}%  max {a.max() * 100:.1f}%")
        # A capture whose frames are almost all sky, or have none at all, usually means
        # the wrong directory or a mis-set threshold rather than an unusual scene.
        if a.mean() < 0.02:
            print("WARNING: almost nothing masked - check --src-subdir and --threshold")
        if a.mean() > 0.85:
            print("WARNING: nearly everything masked - the polarity may be inverted")


if __name__ == "__main__":
    main()
