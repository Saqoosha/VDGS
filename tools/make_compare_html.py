#!/usr/bin/env python3
"""Build a page for comparing two in-game captures frame by frame.

Screenshots of a splat renderer are compared by looking for detail that either survived or
did not, which a side-by-side at fit-to-window size cannot show: the difference lives in
the pixels, so the page opens at 1:1 and lets the two images be wiped against each other
in place. Both halves pan and zoom together, because a difference you have to find twice
is a difference you will misjudge.

    python3 tools/make_compare_html.py <out.html> <label>=<dir> [<label>=<dir> ...]

Each directory is a tools/evalorbit.sh run: shotNNN.png is the first capture, altNNN.png
the second. Images are referenced, not copied, so the page must sit beside them or the
paths must stay valid.
"""

import base64
import html
import json
import mimetypes
import os
import sys


def collect(directory):
    shots = sorted(f for f in os.listdir(directory) if f.startswith('shot') and f.endswith('.png'))
    pairs = []
    for shot in shots:
        alt = 'alt' + shot[4:]
        if os.path.exists(os.path.join(directory, alt)):
            pairs.append((shot, alt))
    return pairs


def data_uri(path):
    kind = mimetypes.guess_type(path)[0] or 'image/png'
    with open(path, 'rb') as f:
        return f'data:{kind};base64,' + base64.b64encode(f.read()).decode('ascii')


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    out = sys.argv[1]
    sets = []
    for arg in sys.argv[2:]:
        label, _, directory = arg.partition('=')
        if not directory:
            sys.exit(f'expected <label>=<dir>, got {arg!r}')
        pairs = collect(directory)
        if not pairs:
            sys.exit(f'{directory}: no shotNNN.png / altNNN.png pairs')
        # Inlined, so the page is one file that survives being moved or sent to someone.
        sets.append({
            'label': label,
            'frames': [{'a': data_uri(os.path.join(directory, a)),
                        'b': data_uri(os.path.join(directory, b))} for a, b in pairs],
        })

    payload = json.dumps(sets)
    total = sum(len(s['frames']) for s in sets)
    page = TEMPLATE.replace('__DATA__', payload).replace('__TITLE__', html.escape(os.path.basename(out)))
    with open(out, 'w') as f:
        f.write(page)
    print(f'{out}  {len(sets)} sets, {total} frames, {os.path.getsize(out) / 1e6:.1f} MB')


TEMPLATE = """<!doctype html>
<html lang="en">
<meta charset="utf-8">
<title>__TITLE__</title>
<style>
  :root { color-scheme: light; --ink: #15171a; --dim: #6b7280; --line: #e5e7eb; }
  body { margin: 0; font: 13px/1.5 ui-sans-serif, system-ui, sans-serif; color: var(--ink); background: #fafafa; }
  /* Dragging is how the page is used, so nothing inside the stage may start a text or
     image selection: the browser's default turns every pan into a highlighted mess. */
  #stage, #stage * { user-select: none; -webkit-user-select: none; }
  header { display: flex; gap: 18px; align-items: baseline; flex-wrap: wrap;
           padding: 12px 18px; border-bottom: 1px solid var(--line); background: #fff; }
  h1 { font-size: 14px; font-weight: 600; margin: 0; }
  .group { display: flex; gap: 6px; align-items: baseline; }
  .group b { font-weight: 500; color: var(--dim); }
  button { font: inherit; padding: 3px 10px; border: 1px solid var(--line); background: #fff;
           border-radius: 999px; cursor: pointer; }
  button[aria-pressed="true"] { background: var(--ink); color: #fff; border-color: var(--ink); }
  .hint { color: var(--dim); margin-left: auto; }
  #stage { position: relative; overflow: hidden; height: calc(100vh - 52px); background: #111;
           cursor: grab; touch-action: none; }
  #stage.drag { cursor: grabbing; }
  .layer { position: absolute; top: 0; left: 0; transform-origin: 0 0; }
  .layer img { display: block; image-rendering: pixelated; -webkit-user-drag: none; pointer-events: none; }
  #top { clip-path: inset(0 50% 0 0); }
  #handle { position: absolute; top: 0; bottom: 0; width: 2px; background: #fff; left: 50%;
            box-shadow: 0 0 0 1px rgba(0,0,0,.35); cursor: ew-resize; }
  #handle::after { content: ""; position: absolute; top: 50%; left: -13px; width: 28px; height: 28px;
                   margin-top: -14px; border-radius: 50%; background: #fff; box-shadow: 0 0 0 1px rgba(0,0,0,.35); }
  .tag { position: absolute; bottom: 10px; padding: 2px 8px; border-radius: 4px; font-size: 12px;
         background: rgba(0,0,0,.65); color: #fff; pointer-events: none; }
  #tagA { left: 10px; } #tagB { right: 10px; }
</style>
<header>
  <h1>LOD comparison</h1>
  <div class="group" id="sets"><b>set</b></div>
  <div class="group" id="frames"><b>frame</b></div>
  <div class="group"><b>zoom</b><span id="zoom">100%</span></div>
  <div class="hint">drag to pan &middot; wheel to zoom &middot; drag the white bar to wipe &middot; press F to fit, 1 for 1:1</div>
</header>
<div id="stage">
  <div class="layer" id="bot"><img id="imgB"></div>
  <div class="layer" id="top"><img id="imgA"></div>
  <div id="handle"></div>
  <span class="tag" id="tagA">ssog (LOD)</span>
  <span class="tag" id="tagB">ply</span>
</div>
<script>
const SETS = __DATA__;
const stage = document.getElementById('stage');
const top_ = document.getElementById('top'), bot = document.getElementById('bot');
const imgA = document.getElementById('imgA'), imgB = document.getElementById('imgB');
const handle = document.getElementById('handle');
let set = 0, frame = 0, scale = 1, x = 0, y = 0, wipe = 0.5;

function buttons(container, labels, get, set_) {
  container.querySelectorAll('button').forEach(b => b.remove());
  labels.forEach((label, i) => {
    const b = document.createElement('button');
    b.textContent = label;
    b.onclick = () => { set_(i); paint(); };
    b.setAttribute('aria-pressed', String(get() === i));
    container.appendChild(b);
  });
}

function load() {
  const f = SETS[set].frames[frame];
  imgA.src = f.a; imgB.src = f.b;
}

function paint() {
  load();
  buttons(document.getElementById('sets'), SETS.map(s => s.label), () => set,
          i => { set = i; frame = Math.min(frame, SETS[i].frames.length - 1); });
  buttons(document.getElementById('frames'), SETS[set].frames.map((_, i) => String(i)),
          () => frame, i => { frame = i; });
  for (const el of [top_, bot]) el.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
  top_.style.clipPath = `inset(0 ${(1 - wipe) * 100}% 0 0)`;
  handle.style.left = (wipe * 100) + '%';
  document.getElementById('zoom').textContent = Math.round(scale * 100) + '%';
}

function fit() {
  const w = imgA.naturalWidth || 1920, h = imgA.naturalHeight || 1080;
  scale = Math.min(stage.clientWidth / w, stage.clientHeight / h);
  x = (stage.clientWidth - w * scale) / 2;
  y = (stage.clientHeight - h * scale) / 2;
  paint();
}

imgA.onload = () => { if (!scale || scale === 1) fit(); };

let dragging = null;
stage.addEventListener('dragstart', e => e.preventDefault());
stage.addEventListener('pointerdown', e => {
  e.preventDefault();
  if (e.target === handle) { dragging = 'wipe'; }
  else { dragging = 'pan'; stage.classList.add('drag'); }
  stage.setPointerCapture(e.pointerId);
});
stage.addEventListener('pointermove', e => {
  if (!dragging) return;
  if (dragging === 'wipe') {
    wipe = Math.min(1, Math.max(0, e.clientX / stage.clientWidth));
  } else {
    x += e.movementX; y += e.movementY;
  }
  paint();
});
stage.addEventListener('pointerup', e => {
  dragging = null; stage.classList.remove('drag'); stage.releasePointerCapture(e.pointerId);
});
stage.addEventListener('wheel', e => {
  e.preventDefault();
  // Zoom about the cursor, so the detail under it stays under it.
  const k = Math.exp(-e.deltaY * 0.002);
  const rect = stage.getBoundingClientRect();
  const cx = e.clientX - rect.left, cy = e.clientY - rect.top;
  x = cx - (cx - x) * k; y = cy - (cy - y) * k;
  scale *= k;
  paint();
}, { passive: false });
addEventListener('keydown', e => {
  if (e.key === 'f' || e.key === 'F') fit();
  if (e.key === '1') { scale = 1; paint(); }
  if (e.key === 'ArrowRight') { frame = (frame + 1) % SETS[set].frames.length; paint(); }
  if (e.key === 'ArrowLeft') { frame = (frame - 1 + SETS[set].frames.length) % SETS[set].frames.length; paint(); }
});
paint();
</script>
</html>
"""


if __name__ == '__main__':
    main()
