#!/usr/bin/env python3
"""空と人のマスクを 1 パスで作り、既存マスクと論理積して書き出す。

**地上の全天球は画面の 46% が空**（JDL の 12 ビューで実測、上向きは 96%）。3DGS は空を
説明しようとして大きく薄いガウシアンを作る。実測では 499 万のうち **93% が死に、生き残りは
最長軸が 70 倍・不透明度 0.05** ——「結果が悪い」の正体がこれ。あとから消すのは閾値の
綱引きになる（上げれば樹冠と電線も消え、下げれば残る）ので、**作られる理由のほうを消す。**

人も同じ理由で消す。歩きながらの撮影では人が動くので、**静的マスクでは獲れない**（時間方向の
std で確認済み）。セグメンテーションが要る。

**空は縮めない（既定 0）、人は広げる。** 縮めるとシルエットに空の縁が帯で残り、そこは
3DGS がいちばん汚い splat を作る場所。縮める意味があるのは細い電線がある会場だけ。
人マスクは小さすぎると輪郭と足元の影が残るので広げる。**非対称なので別々に扱う。**

マスクは COLMAP 規約で **消したい所が黒、残す所が白**。多くの segmenter は逆に出す。

    python3 sky_person_mask.py --images <dir> --out <dir> [--existing <dir>] [--limit 5]
"""
import argparse, os, sys
import numpy as np
from PIL import Image

SKY, PERSON = 2, 12          # ADE20K。ラベルは実行時に検証する


def parse_args():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--images', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--existing', default=None,
                   help='既にあるマスクの置き場（自撮り棒など）。論理積を取る')
    p.add_argument('--model', default='nvidia/segformer-b4-finetuned-ade-512-512')
    p.add_argument('--infer-long-side', type=int, default=1024)
    p.add_argument('--sky-threshold', type=float, default=0.25,
                   help='下げても地面は巻き込まない —— 地面の空確率は 0.0034')
    p.add_argument('--person-threshold', type=float, default=0.35,
                   help='人は取りこぼすほうが痛いので空より低く')
    p.add_argument('--shrink-sky', type=int, default=0, help='空マスクを縮める画素')
    p.add_argument('--cloud-gate', type=float, default=0.05,
                   help='この空確率より上でだけ雲の色ルールを適用する')
    p.add_argument('--cloud-v', type=float, default=185, help='雲とみなす明度の下限')
    p.add_argument('--cloud-s', type=float, default=100, help='雲とみなす彩度の上限')
    p.add_argument('--grow-person', type=int, default=8, help='人マスクを広げる画素')
    p.add_argument('--limit', type=int, default=0)
    p.add_argument('--overwrite', action='store_true')
    return p.parse_args()


def main():
    a = parse_args()
    import cv2, torch
    from transformers import SegformerForSemanticSegmentation, SegformerImageProcessor

    if not os.path.isdir(a.images):
        sys.exit(f'missing: {a.images}')
    os.makedirs(a.out, exist_ok=True)
    names = sorted(n for n in os.listdir(a.images)
                   if n.lower().endswith(('.png', '.jpg', '.jpeg')))
    if a.limit:
        step = max(1, len(names) // a.limit)
        names = names[::step][:a.limit]
    if not names:
        sys.exit(f'no images under {a.images}')

    dev = 'cuda' if torch.cuda.is_available() else 'cpu'
    proc = SegformerImageProcessor.from_pretrained(a.model)
    model = SegformerForSemanticSegmentation.from_pretrained(a.model).to(dev).eval()
    for cid, want in ((SKY, 'sky'), (PERSON, 'person')):
        lbl = model.config.id2label[cid].lower()
        assert lbl.startswith(want), f'class {cid} is {lbl!r}, not {want}'
    print(f'{a.model} on {dev}: {SKY}=sky {PERSON}=person')
    print(f'{len(names)} images -> {a.out}'
          + (f'  (AND {a.existing})' if a.existing else ''))

    ks = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2*a.shrink_sky+1,)*2) if a.shrink_sky else None
    kp = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2*a.grow_person+1,)*2) if a.grow_person else None

    sky_f, per_f, keep_f, missing = [], [], [], 0
    for i, name in enumerate(names):
        out_path = os.path.join(a.out, name + '.png')      # AirVis は <image>.jpg.png
        if os.path.exists(out_path) and not a.overwrite:
            continue
        img = Image.open(os.path.join(a.images, name)).convert('RGB')
        W, H = img.size
        s = a.infer_long_side / max(W, H)
        im_in = img.resize((round(W*s), round(H*s)), Image.LANCZOS) if s < 1.0 else img

        with torch.no_grad():
            logits = model(**proc(images=im_in, return_tensors='pt').to(dev)).logits
        prob = torch.softmax(logits, dim=1)[0]

        # **確率を戻してから閾値。** 先に閾値を切ると、最近傍の折り返しで
        # 幅 1 画素の構造（電線・細い枝）が消える
        sky_p = cv2.resize(prob[SKY].float().cpu().numpy(), (W, H),
                           interpolation=cv2.INTER_LINEAR)
        # **明るい積雲は SegFormer が「壁」や「滝」に分類する。** 閾値を下げるだけでは
        # 本物の構造を食い始めるので、色で第二の軸を足す。雲は明るく彩度が低い
        # （V 中央 239-254 / S 中央 18-55）のに対し、芝と木は V 94-106 / S 100-102。
        # **空確率で門をつける** —— 芝の上の白い旗も明るく彩度が低いが、そちらの空確率は
        # ほぼ 0（0.0034）。門が無いと旗が全ビューで消えて復元されなくなる。
        hsv = cv2.cvtColor(np.asarray(img), cv2.COLOR_RGB2HSV)
        cloud = (sky_p > a.cloud_gate) & (hsv[:, :, 2] > a.cloud_v) & (hsv[:, :, 1] < a.cloud_s)
        sky = ((sky_p > a.sky_threshold) | cloud).astype(np.uint8)
        per = (cv2.resize(prob[PERSON].float().cpu().numpy(), (W, H),
                          interpolation=cv2.INTER_LINEAR) > a.person_threshold).astype(np.uint8)
        if ks is not None:
            sky = cv2.erode(sky, ks, iterations=1)
        if kp is not None:
            per = cv2.dilate(per, kp, iterations=1)

        keep = ((1 - np.maximum(sky, per)) * 255).astype(np.uint8)
        if a.existing:
            ep = os.path.join(a.existing, name + '.png')
            if os.path.exists(ep):
                old = np.asarray(Image.open(ep).convert('L').resize((W, H), Image.NEAREST))
                keep = np.minimum(keep, old)          # 論理積: 片方が黒なら黒
            else:
                missing += 1
        Image.fromarray(keep, mode='L').save(out_path, optimize=True)

        sky_f.append(sky.mean()); per_f.append(per.mean()); keep_f.append((keep > 127).mean())
        if i < 3 or (i+1) % 200 == 0 or i == len(names)-1:
            print(f'  [{i+1}/{len(names)}] {name[:40]:40s} 空 {sky.mean()*100:5.1f}%'
                  f'  人 {per.mean()*100:4.1f}%  残す {(keep>127).mean()*100:5.1f}%')

    if sky_f:
        f = lambda v: f'中央 {np.median(v)*100:.1f}%  最大 {max(v)*100:.1f}%'
        print(f'\n空   {f(sky_f)}')
        print(f'人   {f(per_f)}')
        print(f'残す {f(keep_f)}   最小 {min(keep_f)*100:.1f}%')
        if missing:
            print(f'** 既存マスクが見つからなかった画像 {missing} 枚 **')


main()
