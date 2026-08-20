namespace VDGS
{
    /// <summary>
    /// The browser UI, embedded so the whole mod stays a single DLL.
    ///
    /// Deliberately plain: no build step, no framework, no external requests (the game
    /// machine may not have internet, and a CDN font is not worth a dependency).
    /// </summary>
    internal static class WebUi
    {
        internal const string Html = @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>VDGS Control</title>
<style>
  :root {
    --bg: #f6f7f9;
    --card: #ffffff;
    --line: #e2e5ea;
    --text: #1c2024;
    --muted: #6b7280;
    --accent: #2563eb;
    --accent-soft: #eff4ff;
    --ok: #15803d;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 24px;
    background: var(--bg); color: var(--text);
    font: 15px/1.5 -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
  }
  .wrap { max-width: 720px; margin: 0 auto; }
  h1 { font-size: 20px; margin: 0 0 20px; letter-spacing: -0.01em; }
  h1 .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%;
            background: #cbd5e1; margin-right: 8px; vertical-align: middle; }
  h1 .dot.live { background: var(--ok); }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 10px;
          padding: 18px; margin-bottom: 16px; }
  .label { font-size: 12px; text-transform: uppercase; letter-spacing: 0.06em;
           color: var(--muted); margin-bottom: 6px; }
  .track { font-size: 18px; font-weight: 600; word-break: break-word; }
  .track.none { color: var(--muted); font-weight: 400; }
  .bound { margin-top: 8px; font-size: 13px; color: var(--muted); }
  .bound b { color: var(--text); }
  ul { list-style: none; margin: 0; padding: 0; }
  li { display: flex; align-items: center; gap: 12px; padding: 10px 12px;
       border: 1px solid var(--line); border-radius: 8px; margin-bottom: 8px; }
  li.active { border-color: var(--accent); background: var(--accent-soft); }
  li .name { font-weight: 600; }
  li .meta { color: var(--muted); font-size: 13px; margin-left: auto; }
  button { font: inherit; padding: 7px 14px; border-radius: 7px; cursor: pointer;
           border: 1px solid var(--line); background: #fff; color: var(--text); }
  button:hover { border-color: #c3c9d2; }
  button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
  button.primary:hover { background: #1d4fd8; }
  button:disabled { opacity: .45; cursor: default; }
  .row { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
  .hint { color: var(--muted); font-size: 13px; margin-top: 10px; }
  .empty { color: var(--muted); padding: 6px 0; }
  table { width: 100%; border-collapse: collapse; font-size: 14px; }
  td { padding: 6px 0; border-bottom: 1px solid var(--line); vertical-align: top; }
  td:first-child { font-weight: 600; padding-right: 16px; word-break: break-word; }
  tr:last-child td { border-bottom: 0; }
  .flash { color: var(--ok); font-size: 13px; margin-left: 4px; }
  .xform { display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
           margin: 8px 0 2px 0; padding: 10px 12px; border: 1px solid var(--accent);
           border-radius: 8px; background: var(--accent-soft); }
  .xform label { font-size: 12px; color: var(--muted); text-transform: uppercase;
                 letter-spacing: 0.05em; }
  .xform input[type=range] { flex: 1; min-width: 130px; }
  .xform input[type=number] { width: 84px; font: inherit; padding: 5px 7px;
                              border: 1px solid var(--line); border-radius: 6px; }
  .xform .val { font-variant-numeric: tabular-nums; min-width: 58px; text-align: right;
                font-weight: 600; }
</style>
</head>
<body>
<div class=""wrap"">
  <h1><span class=""dot"" id=""dot""></span>VDGS Control</h1>

  <div class=""card"">
    <div class=""label"">Current track</div>
    <div class=""track none"" id=""track"">-</div>
    <div class=""bound"" id=""boundto""></div>
  </div>

  <div class=""card"">
    <div class=""label"">Splat scenes on this machine</div>
    <ul id=""list""><li class=""empty"">loading…</li></ul>
    <div id=""xform"" style=""display:none"">
      <div class=""xform"">
        <label>Scale</label>
        <input type=""range"" id=""scaleRange"" min=""-2"" max=""2"" step=""0.002"" value=""0"">
        <span class=""val"" id=""scaleVal"">1.00</span>
        <input type=""number"" id=""scaleNum"" step=""0.01"" min=""0.01"" max=""100"">
      </div>
      <div class=""xform"">
        <label>Height</label>
        <input type=""range"" id=""yRange"" min=""-1"" max=""1"" step=""0.001"" value=""0"">
        <span class=""val"" id=""yVal"">0.00</span>
        <input type=""number"" id=""yNum"" step=""0.05"" min=""-1000"" max=""1000"">
      </div>
    </div>

    <div class=""row"" style=""margin-top:14px"">
      <button class=""primary"" id=""bind"">Bind shown splat to this track</button>
      <button id=""unbind"">Unbind this track</button>
      <button id=""unload"">Hide all</button>
      <span class=""flash"" id=""flash""></span>
    </div>
    <div class=""hint"">
      Copy converted captures into &lt;game&gt;/vdgs/ first (tools/deploy.sh); they appear here automatically.
    </div>
  </div>

  <div class=""card"">
    <div class=""label"">Bindings</div>
    <table id=""bindings""><tr><td class=""empty"">none</td></tr></table>
  </div>
</div>

<script>
let state = { available: [], loaded: [], track: null, bindings: {} };
let busy = false;
let dragging = false;

// Scale runs over a wide range, so the slider is logarithmic: linear steps would
// make everything below 1 unusable while the top half did nothing useful.
const toSlider = v => Math.log10(Math.max(0.01, v));
const fromSlider = v => Math.pow(10, v);

// Height has the same problem in both directions. It was linear over -5..+5, which is
// right for a room and useless for a site: textilni's ground floor sits 5.11 m below its
// origin, so lining it up with the game's ground plane needed a value the slider could
// not reach at all. Widening it linearly would then make the room case unusable - a
// pixel of travel would be tens of centimetres.
//
// So the same trick, signed: fine near zero, far reach at the ends. A third of the travel
// each way covers +/-4.9 m, and the ends reach +/-200 m. Resolution per slider step is
// 5 mm at 0, 3 cm at 5 m, 27 cm at 50 m. The number box beside it takes an exact value and
// goes to +/-1000 - which utlida needs, its floor being 206 m below its own origin.
const kYReach = 200;
const toYSlider = v => Math.sign(v) * Math.log1p(Math.abs(v)) / Math.log1p(kYReach);
const fromYSlider = t => Math.sign(t) * Math.expm1(Math.abs(t) * Math.log1p(kYReach));

function setScaleUi(v) {
  document.getElementById('scaleRange').value = toSlider(v);
  document.getElementById('scaleNum').value = Number(v).toFixed(3);
  document.getElementById('scaleVal').textContent = Number(v).toFixed(2) + 'x';
}
function setYUi(v) {
  document.getElementById('yRange').value = toYSlider(Math.max(-kYReach, Math.min(kYReach, v)));
  document.getElementById('yNum').value = Number(v).toFixed(2);
  document.getElementById('yVal').textContent = Number(v).toFixed(2) + 'm';
}

async function pushTransform(scale, y) {
  const name = (state.loaded || [])[0];
  if (!name) return;
  const body = { splat: name };
  if (scale != null) body.scale = scale;
  if (y != null) body.y = y;
  await fetch('/api/transform', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
}

async function refresh() {
  try {
    const r = await fetch('/api/status', { cache: 'no-store' });
    state = await r.json();
    document.getElementById('dot').classList.add('live');
  } catch (e) {
    document.getElementById('dot').classList.remove('live');
    return;
  }
  render();
}

function render() {
  const t = document.getElementById('track');
  if (state.track) { t.textContent = state.track; t.classList.remove('none'); }
  else { t.textContent = 'no track loaded'; t.classList.add('none'); }

  const bound = state.track ? (state.bindings || {})[state.track] : null;
  const boundEl = document.getElementById('boundto');
  boundEl.replaceChildren();
  if (state.track) {
    if (bound && bound.length) {
      boundEl.append('bound to ');
      const b = document.createElement('b');
      b.textContent = bound.join(', ');
      boundEl.appendChild(b);
    } else {
      boundEl.textContent = 'not bound to any splat';
    }
  }

  // Built with DOM APIs, never innerHTML: track names come from community tracks
  // downloaded inside the game, so they are attacker-controlled strings.
  const list = document.getElementById('list');
  list.replaceChildren();
  if (!state.available || !state.available.length) {
    const li = document.createElement('li');
    li.className = 'empty';
    li.textContent = 'nothing in <game>/vdgs/';
    list.appendChild(li);
  } else {
    for (const s of state.available) {
      const shown = (state.loaded || []).indexOf(s.name) >= 0;
      const li = document.createElement('li');
      if (shown) li.className = 'active';

      const btn = document.createElement('button');
      btn.disabled = shown;
      btn.dataset.load = s.name;
      btn.textContent = shown ? 'shown' : 'show';

      const name = document.createElement('span');
      name.className = 'name';
      name.textContent = s.name;

      const meta = document.createElement('span');
      meta.className = 'meta';
      meta.textContent = s.splats ? s.splats.toLocaleString() + ' splats' : '';

      // Seals the capture inside a black box so the game's terrain, fog and horizon
      // stop showing through the holes every reconstruction has.
      const boxLabel = document.createElement('label');
      boxLabel.className = 'meta';
      boxLabel.title = 'black box around this capture';
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = !!s.backdrop;
      box.addEventListener('change', async () => {
        box.disabled = true;
        await fetch('/api/backdrop', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ splat: s.name, on: box.checked }),
        });
        box.disabled = false;
        refresh();
      });
      boxLabel.append(box, document.createTextNode(' box'));

      li.append(btn, name, meta, boxLabel);

      // Only for captures that have a collision mesh. A disabled checkbox on the rest
      // would read as solid-but-switched-off, when the truth is that no mesh exists.
      if (s.hasCollision) {
        const solidLabel = document.createElement('label');
        solidLabel.className = 'meta';
        solidLabel.title = 'walls and floor stop the drone';
        const solid = document.createElement('input');
        solid.type = 'checkbox';
        solid.checked = !!s.collision;
        solid.addEventListener('change', async () => {
          solid.disabled = true;
          await fetch('/api/collision', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ splat: s.name, on: solid.checked }),
          });
          solid.disabled = false;
          refresh();
        });
        solidLabel.append(solid, document.createTextNode(' solid'));
        li.appendChild(solidLabel);

        // Draws the collision shell in-game. Solid culls back faces, which is the
        // orientation test: inside a correctly wound room the walls are visible, inside an
        // inside-out one nothing is.
        const view = document.createElement('select');
        view.className = 'meta';
        view.title = 'draw the collision mesh';
        for (const m of ['off', 'solid', 'wire']) {
          const opt = document.createElement('option');
          opt.value = m;
          opt.textContent = m === 'off' ? 'hide mesh' : 'show ' + m;
          view.appendChild(opt);
        }
        view.value = s.collisionView || 'off';
        view.addEventListener('change', async () => {
          view.disabled = true;
          await fetch('/api/collisionview', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ splat: s.name, mode: view.value }),
          });
          view.disabled = false;
          refresh();
        });
        li.appendChild(view);
      }

      list.appendChild(li);
    }
  }

  const tbl = document.getElementById('bindings');
  tbl.replaceChildren();
  const keys = Object.keys(state.bindings || {});
  if (!keys.length) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.className = 'empty';
    td.textContent = 'none';
    tr.appendChild(td);
    tbl.appendChild(tr);
  } else {
    for (const k of keys) {
      const tr = document.createElement('tr');

      const tdTrack = document.createElement('td');
      tdTrack.textContent = k;

      const tdSplats = document.createElement('td');
      tdSplats.textContent = state.bindings[k].join(', ');

      const tdBtn = document.createElement('td');
      tdBtn.style.width = '1%';
      const rm = document.createElement('button');
      rm.dataset.unbind = k;
      rm.textContent = 'remove';
      tdBtn.appendChild(rm);

      tr.append(tdTrack, tdSplats, tdBtn);
      tbl.appendChild(tr);
    }
  }

  // Transform only makes sense for something on screen, and only while the user is
  // not dragging - otherwise the 1.5s poll snaps the slider out from under them.
  const shownName = (state.loaded || [])[0];
  const shown = (state.available || []).find(s => s.name === shownName);
  const panel = document.getElementById('xform');
  if (shown && !dragging) {
    panel.style.display = '';
    setScaleUi(shown.scale != null ? shown.scale : 1);
    setYUi(shown.y != null ? shown.y : 0);
  } else if (!shown) {
    panel.style.display = 'none';
  }

  document.getElementById('bind').disabled = !state.track;
  document.getElementById('unbind').disabled =
    !state.track || !(state.bindings || {})[state.track];
}

async function post(url, body) {
  if (busy) return;
  busy = true;
  try {
    await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    });
    // The action runs on the game's next frame, so give it one before re-reading.
    await new Promise(r => setTimeout(r, 250));
    await refresh();
  } finally { busy = false; }
}

function flash(msg) {
  const el = document.getElementById('flash');
  el.textContent = msg;
  setTimeout(() => { el.textContent = ''; }, 2000);
}

document.addEventListener('click', async e => {
  if (!e.target.getAttribute) return;

  const load = e.target.getAttribute('data-load');
  if (load) { post('/api/load', { splat: load }); return; }

  const unbind = e.target.getAttribute('data-unbind');
  if (unbind) { await post('/api/unbind', { track: unbind }); flash('removed'); }
});
// Live drag: apply continuously so the change is visible in the sim as it happens.
for (const [range, num, isScale] of [['scaleRange','scaleNum',true], ['yRange','yNum',false]]) {
  const r = document.getElementById(range), n = document.getElementById(num);
  r.addEventListener('pointerdown', () => { dragging = true; });
  r.addEventListener('pointerup',   () => { dragging = false; });
  r.addEventListener('input', () => {
    const t = parseFloat(r.value);
    const v = isScale ? fromSlider(t) : fromYSlider(t);
    if (isScale) setScaleUi(v); else setYUi(v);
    pushTransform(isScale ? v : null, isScale ? null : v);
  });
  n.addEventListener('change', () => {
    const v = parseFloat(n.value);
    if (!isFinite(v)) return;
    if (isScale) setScaleUi(v); else setYUi(v);
    pushTransform(isScale ? v : null, isScale ? null : v);
  });
}

document.getElementById('unload').addEventListener('click', () => post('/api/unload'));
document.getElementById('bind').addEventListener('click', async () => {
  await post('/api/bind', { splats: state.loaded || [] });
  flash('saved');
});
document.getElementById('unbind').addEventListener('click', async () => {
  await post('/api/unbind', {});
  flash('unbound');
});

refresh();
setInterval(refresh, 1500);
</script>
</body>
</html>
";
    }
}
