#!/usr/bin/env python3
"""マスクされる対象をクラスごとに色分けして 1 枚にする。**合成後の白黒では何が消えたか分からない。**

空＝青、人＝赤、AirVis の既存マスク（自撮り棒とマウント）＝黄。
重なりは後勝ちではなく、それぞれの寄与が見えるように別々に塗る。
"""
import os, sys
import numpy as np
import torch
from PIL import Image
from transformers import SegformerForSemanticSegmentation, SegformerImageProcessor

# The AirVis project lives wherever the app was pointed at, so it comes from the
# environment rather than being written down here - a path with someone's username in it
# is both a leak and a script nobody else can run.
ROOT = os.environ.get('VDGS_AIRVIS_PROJECT') or (sys.argv[1] if len(sys.argv) > 1 else '')
if not ROOT:
    sys.exit('set VDGS_AIRVIS_PROJECT, or pass the project directory as the first argument\n'
             '       (the one holding Extracted/sfm-images-1600 and Extracted/sfm-masks-1600)')
D = os.path.join(ROOT, 'Extracted', 'sfm-images-1600') + os.sep
M = os.path.join(ROOT, 'Extracted', 'sfm-masks-1600') + os.sep
PICKS = [
    ('yaw+000_pitch-060__VID_20260821_173728_00_007-f000347.jpg', 'down: mount + person'),
    ('yaw+045_pitch+000__VID_20260821_173728_00_007-f000347.jpg', 'horizon: sky'),
    ('yaw+000_pitch+060__VID_20260821_173728_00_007-f000347.jpg', 'up: sky'),
]
SKY, PERSON = 2, 12
mdl = 'nvidia/segformer-b4-finetuned-ade-512-512'
proc = SegformerImageProcessor.from_pretrained(mdl)
net = SegformerForSemanticSegmentation.from_pretrained(mdl).cuda().eval()

SZ = 400
rows = []
for name, _lbl in PICKS:
    if not os.path.exists(D + name):
        print('missing', name); continue
    img = Image.open(D + name).convert('RGB')
    W, H = img.size
    with torch.no_grad():
        pr = torch.softmax(net(**proc(images=img.resize((1024, 1024)),
                                     return_tensors='pt').to('cuda')).logits, 1)[0]
    import cv2
    sky = cv2.resize(pr[SKY].float().cpu().numpy(), (W, H)) > 0.5
    per = cv2.resize(pr[PERSON].float().cpu().numpy(), (W, H)) > 0.35
    if per.any():
        k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (17, 17))
        per = cv2.dilate(per.astype(np.uint8), k, 1).astype(bool)
    old = np.zeros((H, W), bool)
    if os.path.exists(M + name + '.png'):
        old = np.asarray(Image.open(M + name + '.png').convert('L')) < 128

    a = np.asarray(img).astype(np.float64)
    ov = a.copy()
    ov[sky] = ov[sky]*0.25 + np.array([0, 90, 255])*0.75      # 空 = 青
    ov[per] = ov[per]*0.25 + np.array([255, 40, 40])*0.75     # 人 = 赤
    ov[old] = ov[old]*0.25 + np.array([255, 210, 0])*0.75     # マウント = 黄
    keep = ~(sky | per | old)
    fin = a.copy(); fin[~keep] = 0                            # 最終: 残る所だけ

    trio = [Image.fromarray(x.astype(np.uint8)).resize((SZ, SZ))
            for x in (a, ov, fin)]
    rows.append((np.concatenate([np.asarray(t) for t in trio], 1),
                 f'{name.split("__")[0]:22s} sky {sky.mean()*100:4.1f}%  '
                 f'person {per.mean()*100:4.1f}%  mount {old.mean()*100:4.1f}%  '
                 f'keep {keep.mean()*100:5.1f}%'))
    print(rows[-1][1])

if rows:
    HDR = 18
    W = rows[0][0].shape[1]
    sheet = Image.new('RGB', (W, (SZ+HDR)*len(rows)), (20, 20, 20))
    from PIL import ImageDraw
    d = ImageDraw.Draw(sheet)
    for i, (im, lbl) in enumerate(rows):
        d.text((4, i*(SZ+HDR)+3), lbl, fill=(225, 225, 225))
        sheet.paste(Image.fromarray(im), (0, i*(SZ+HDR)+HDR))
    sheet.save('/tmp/maskpreview.png')
    print('wrote /tmp/maskpreview.png', sheet.size)
