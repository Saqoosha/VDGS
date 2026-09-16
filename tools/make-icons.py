#!/usr/bin/env python3
"""Bake the companion's icon set from companion-tauri/icon-src/master.png (1024x1024, full-bleed).

    python3 tools/make-icons.py

Writes into companion-tauri/src-tauri/icons/:
  icon.icon/    Icon Composer bundle - one full-bleed layer, no glass, no shadow. macOS 26+
                reads this (Tauri compiles it to Assets.car with actool >= 26) and applies its
                own squircle. A legacy .icns with transparent margins is what earns the gray
                "unadopted icon" base on those releases, so this is not optional.
  icon.icns     Apple's pre-26 template: 824/1024 rounded square with transparent margin. The
                fallback for macOS 15 and earlier, which does not mask and would show a
                full-bleed icns as a sharp-cornered square.
  icon.ico      Full-bleed square, 16..256. Windows never masks.
  icon.png, 32x32.png, 64x64.png, 128x128.png, 128x128@2x.png
                Template-shaped, the layout Tauri's own `tauri icon` produces.

Needs Pillow and iconutil (macOS). The wordmark is Fraunces Variable italic, the same face
as web/src/chrome.tsx; master.png is the composited result, so the font is not needed here.
"""
import json, os, shutil, subprocess, sys
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / 'companion-tauri/icon-src/master.png'
OUT = ROOT / 'companion-tauri/src-tauri/icons'
master = Image.open(SRC).convert('RGBA')
if master.size != (1024, 1024):
    sys.exit(f'{SRC} is {master.size}, want 1024x1024')

def shaped(size):
    s = 4; c = 1024 * s; inner = 824 * s; r = 185 * s; m = (c - inner) // 2
    mask = Image.new('L', (c, c), 0)
    ImageDraw.Draw(mask).rounded_rectangle([m, m, m + inner, m + inner], radius=r, fill=255)
    canvas = Image.new('RGBA', (c, c), (0, 0, 0, 0))
    canvas.paste(master.resize((inner, inner), Image.LANCZOS), (m, m))
    canvas.putalpha(mask)
    return canvas.resize((size, size), Image.LANCZOS)

def square(size):
    return master.resize((size, size), Image.LANCZOS)

shaped(1024).save(OUT / 'icon.png')
for name, size in (('32x32', 32), ('64x64', 64), ('128x128', 128), ('128x128@2x', 256)):
    shaped(size).save(OUT / f'{name}.png')

iconset = OUT / 'vdgs.iconset'
shutil.rmtree(iconset, ignore_errors=True); iconset.mkdir()
for size in (16, 32, 128, 256, 512):
    shaped(size).save(iconset / f'icon_{size}x{size}.png')
    shaped(size * 2).save(iconset / f'icon_{size}x{size}@2x.png')
subprocess.run(['iconutil', '-c', 'icns', str(iconset), '-o', str(OUT / 'icon.icns')], check=True)
shutil.rmtree(iconset)

sizes = [(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]
square(256).save(OUT / 'icon.ico', format='ICO', sizes=sizes, append_images=[square(s) for s, _ in sizes[:-1]])

icon_dir = OUT / 'icon.icon'
shutil.rmtree(icon_dir, ignore_errors=True); (icon_dir / 'Assets').mkdir(parents=True)
master.convert('RGB').save(icon_dir / 'Assets/master.png', optimize=True)
(icon_dir / 'icon.json').write_text(json.dumps({
    'fill': {'solid': 'srgb:0.01961,0.02745,0.04706,1.00000'},
    'groups': [{
        'layers': [{'glass': False, 'hidden': False, 'image-name': 'master.png', 'name': 'master'}],
        'shadow': {'kind': 'neutral', 'opacity': 0.0},
        'translucency': {'enabled': False, 'value': 0.0},
    }],
    'supported-platforms': {'circles': ['watchOS'], 'squares': 'shared'},
}, indent=2) + '\n')
print('ok:', ', '.join(sorted(p.name for p in OUT.iterdir())))
