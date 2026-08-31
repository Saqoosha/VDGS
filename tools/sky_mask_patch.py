#!/usr/bin/env python3
"""Teach gsplat's example trainer to read per-image masks. Idempotent; keeps a .orig.

gsplat has no per-image mask support: the COLMAP parser takes no mask argument and never
opens one, and the single `mask_dict` it does have is keyed by camera_id and only ever
holds the valid-ROI of a fisheye undistortion. So this patches the two example files.

Three edits, and the third is the one that matters:

  1. Parser gains `mask_dir` and builds a mask path per image.
  2. Dataset.__getitem__ loads that mask and puts it through the SAME undistort remap,
     ROI crop and patch crop as the image, then ANDs it with any fisheye ROI.
  3. train() computes L1 and SSIM over kept pixels only.

Edit 3 is not optional. The trainer already zeroes the render inside the mask
(`render_colors[~masks] = 0`), which looks like it is enough and is not: the mean still
divides by every pixel, so with sky over a third of the frame the photometric pull on the
ground drops by that fraction while opacity_reg and scale_reg do not - the regularisers
quietly get half again as strong. And zeroing only the render, not the ground truth,
injects gradient into a ~6 pixel band of kept ground along the sky boundary even where the
render is already exact. Both sides, or not at all.

    python3 sky_mask_patch.py --examples-dir ~/gsplat/examples
    python3 sky_mask_patch.py --examples-dir ~/gsplat/examples --revert
"""

import argparse
import os
import shutil
import sys

MARK = "VDGS-SKY-MASK"


def edit(text, old, new, what):
    # "Already applied?" has to be judged on a line the edit ADDS, never on the first line
    # of the replacement: most of these edits re-emit their anchor as context, so testing
    # that line finds it in the unpatched file and skips the edit while reporting success.
    added = [ln.strip() for ln in new.splitlines()
             if ln.strip() and ln.strip() not in old and len(ln.strip()) > 12]
    if not added:
        sys.exit(f"{what}: replacement adds nothing recognisable; refusing to guess")
    # The longest added line, not the first: several of these edits open with the same
    # `if sky_keep is not None:` guard, so the first line is not unique between them.
    probe = max(added, key=len)
    if probe in text:
        print(f"  = {what}: already patched")
        return text, False
    if old not in text:
        sys.exit(f"anchor not found for {what}; refusing to guess:\n---\n{old}\n---")
    print(f"  + {what}")
    return text.replace(old, new, 1), True


def patch_colmap(path):
    src = open(path).read()
    changed = False

    src, c = edit(
        src,
        "        factor: int = 1,",
        "        factor: int = 1,\n"
        "        mask_dir: Optional[str] = None,  # " + MARK + ": per-image masks, white = keep",
        "Parser: mask_dir argument",
    )
    changed |= c

    src, c = edit(
        src,
        "        self.factor = factor",
        "        self.factor = factor\n"
        "        self.mask_dir = mask_dir  # " + MARK,
        "Parser: store mask_dir",
    )
    changed |= c

    # image_paths is where the per-image ordering is settled, so the mask list is built
    # from the same names rather than by globbing the mask directory - a missing mask has
    # to fail loudly, not silently shift every later image onto the wrong mask.
    src, c = edit(
        src,
        "        self.image_paths = image_paths  # List[str], (num_images,)",
        "        self.image_paths = image_paths  # List[str], (num_images,)\n"
        "        # " + MARK + ": one mask per image, matched by stem, in the same order.\n"
        "        self.sky_mask_paths = None\n"
        "        if mask_dir is not None:\n"
        "            self.sky_mask_paths = []\n"
        "            for p in image_paths:\n"
        "                stem = os.path.splitext(os.path.basename(p))[0]\n"
        "                mp = os.path.join(mask_dir, stem + '.png')\n"
        "                if not os.path.exists(mp):\n"
        "                    raise FileNotFoundError(\n"
        "                        f'mask missing for {os.path.basename(p)}: {mp}')\n"
        "                self.sky_mask_paths.append(mp)\n"
        "            print(f'[" + MARK + "] {len(self.sky_mask_paths)} masks from {mask_dir}')",
        "Parser: build mask paths",
    )
    changed |= c

    src, c = edit(
        src,
        "        mask = self.parser.mask_dict[camera_id]\n",
        "        mask = self.parser.mask_dict[camera_id]\n"
        "        # " + MARK + ": load this image's mask before the geometry below, so it\n"
        "        # goes through the identical remap and crops.\n"
        "        sky_keep = None\n"
        "        if self.parser.sky_mask_paths is not None:\n"
        "            sky_keep = imageio.imread(self.parser.sky_mask_paths[index])\n"
        "            if sky_keep.ndim == 3:\n"
        "                sky_keep = sky_keep[..., 0]\n"
        "            if sky_keep.shape[:2] != image.shape[:2]:\n"
        "                sky_keep = cv2.resize(\n"
        "                    sky_keep, (image.shape[1], image.shape[0]),\n"
        "                    interpolation=cv2.INTER_AREA)\n",
        "Dataset: load mask",
    )
    changed |= c

    src, c = edit(
        src,
        "            image = cv2.remap(image, mapx, mapy, cv2.INTER_LINEAR)\n"
        "            x, y, w, h = self.parser.roi_undist_dict[camera_id]\n"
        "            image = image[y : y + h, x : x + w]\n",
        "            image = cv2.remap(image, mapx, mapy, cv2.INTER_LINEAR)\n"
        "            x, y, w, h = self.parser.roi_undist_dict[camera_id]\n"
        "            image = image[y : y + h, x : x + w]\n"
        "            if sky_keep is not None:  # " + MARK + "\n"
        "                sky_keep = cv2.remap(sky_keep, mapx, mapy, cv2.INTER_LINEAR)\n"
        "                sky_keep = sky_keep[y : y + h, x : x + w]\n",
        "Dataset: undistort mask with the image",
    )
    changed |= c

    src, c = edit(
        src,
        "            image = image[y : y + self.patch_size, x : x + self.patch_size]\n"
        "            K[0, 2] -= x\n"
        "            K[1, 2] -= y\n",
        "            image = image[y : y + self.patch_size, x : x + self.patch_size]\n"
        "            if sky_keep is not None:  # " + MARK + "\n"
        "                sky_keep = sky_keep[y : y + self.patch_size,\n"
        "                                    x : x + self.patch_size]\n"
        "            K[0, 2] -= x\n"
        "            K[1, 2] -= y\n",
        "Dataset: crop mask with the image",
    )
    changed |= c

    # Thresholded last, after every resample, so a soft edge never turns into fractional
    # mask values. Combined with the fisheye ROI rather than replacing it.
    src, c = edit(
        src,
        "        if mask is not None:\n"
        "            data[\"mask\"] = torch.from_numpy(mask).bool()\n",
        "        if sky_keep is not None:  # " + MARK + "\n"
        "            keep = sky_keep > 127\n"
        "            mask = keep if mask is None else np.logical_and(mask, keep)\n"
        "        if mask is not None:\n"
        "            data[\"mask\"] = torch.from_numpy(mask).bool()\n",
        "Dataset: emit combined mask",
    )
    changed |= c
    return src, changed


def patch_trainer(path):
    src = open(path).read()
    changed = False

    src, c = edit(
        src,
        "    data_factor: int = 4",
        "    data_factor: int = 4\n"
        "    # " + MARK + ": directory of per-image masks, white = keep, black = drop.\n"
        "    mask_dir: Optional[str] = None\n"
        "    # " + MARK + ": weight on accumulated alpha inside the mask, pushing it to 0.\n"
        "    sky_lambda: float = 0.1",
        "Config: mask_dir",
    )
    changed |= c

    src, c = edit(
        src,
        "            factor=cfg.data_factor,",
        "            factor=cfg.data_factor,\n"
        "            mask_dir=cfg.mask_dir,  # " + MARK,
        "Runner: pass mask_dir to Parser",
    )
    changed |= c

    # The whole point of the patch. Renormalising over kept pixels keeps the photometric
    # term at full strength relative to the regularisers, and masking both sides of SSIM
    # stops the boundary band from being pulled toward black.
    src, c = edit(
        src,
        "            l1loss = F.l1_loss(colors, pixels)\n",
        "            # " + MARK + ": over kept pixels only. Zeroing the render alone\n"
        "            # leaves the mean dividing by every pixel, which weakens the\n"
        "            # photometric pull on what is left while the regularisers stay put.\n"
        "            if masks is not None:\n"
        "                sel = masks[..., None].expand_as(colors)\n"
        "                l1loss = F.l1_loss(colors[sel], pixels[sel])\n"
        "                colors_ssim = colors * masks[..., None]\n"
        "                pixels_ssim = pixels * masks[..., None]\n"
        "            else:\n"
        "                l1loss = F.l1_loss(colors, pixels)\n"
        "                colors_ssim, pixels_ssim = colors, pixels\n",
        "train: masked L1",
    )
    changed |= c

    # Masking the photometric loss stops the sky from being CREATED; it does nothing about
    # what drifts in later. Those gaussians sit in a region with no gradient at all, so
    # whatever opacity and colour they happen to hold is never corrected - the first run
    # came back with dark streaks across an otherwise empty sky. Penalising accumulated
    # alpha there gives the region a gradient again, in the one direction that is always
    # right: transparent. MCMC then recycles them, so the sky's share of the budget goes
    # back to the ground rather than being abandoned.
    src, c = edit(
        src,
        "            loss = l1loss * (1.0 - cfg.ssim_lambda) + ssimloss * cfg.ssim_lambda\n",
        "            loss = l1loss * (1.0 - cfg.ssim_lambda) + ssimloss * cfg.ssim_lambda\n"
        "            if masks is not None and cfg.sky_lambda > 0:  # " + MARK + "\n"
        "                sky = ~masks\n"
        "                # Most frames of a drone capture point straight down and hold no\n"
        "                # sky at all; the mean of an empty selection is NaN.\n"
        "                if sky.any():\n"
        "                    loss = loss + cfg.sky_lambda * alphas[..., 0][sky].mean()\n",
        "train: sky opacity penalty",
    )
    changed |= c

    src, c = edit(
        src,
        "            ssimloss = 1.0 - fused_ssim(\n"
        "                colors.permute(0, 3, 1, 2), pixels.permute(0, 3, 1, 2), padding=\"valid\"\n"
        "            )\n",
        "            ssimloss = 1.0 - fused_ssim(\n"
        "                colors_ssim.permute(0, 3, 1, 2),  # " + MARK + ": both sides\n"
        "                pixels_ssim.permute(0, 3, 1, 2),\n"
        "                padding=\"valid\",\n"
        "            )\n",
        "train: masked SSIM",
    )
    changed |= c
    return src, changed


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--examples-dir", required=True)
    ap.add_argument("--revert", action="store_true")
    args = ap.parse_args()

    targets = [os.path.join(args.examples_dir, "datasets", "colmap.py"),
               os.path.join(args.examples_dir, "simple_trainer.py")]
    for t in targets:
        if not os.path.exists(t):
            sys.exit(f"not found: {t}")

    if args.revert:
        for t in targets:
            orig = t + ".orig"
            if os.path.exists(orig):
                shutil.copy2(orig, t)
                print(f"reverted {t}")
            else:
                print(f"no backup for {t}; left alone")
        return

    # This checkout is a deliberate mixture - most of examples/ was rolled back to the
    # 1.5.3 release while datasets/colmap.py carries unpushed local work - so a git
    # checkout would destroy both. Back up by copy and never touch git here.
    for t, fn in zip(targets, (patch_colmap, patch_trainer)):
        print(os.path.basename(t) + ":")
        orig = t + ".orig"
        if not os.path.exists(orig):
            shutil.copy2(t, orig)
            print(f"  . backed up to {os.path.basename(orig)}")
        out, changed = fn(t)
        if changed:
            open(t, "w").write(out)
    print("\npatched. Train with --mask_dir <data_dir>/masks")


if __name__ == "__main__":
    main()
