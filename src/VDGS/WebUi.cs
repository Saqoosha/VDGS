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
  document.getElementById('boundto').innerHTML =
    !state.track ? '' :
    (bound && bound.length) ? 'bound to <b>' + bound.join(', ') + '</b>'
                            : 'not bound to any splat';

  const list = document.getElementById('list');
  list.innerHTML = '';
  if (!state.available || !state.available.length) {
    list.innerHTML = '<li class=""empty"">nothing in &lt;game&gt;/vdgs/</li>';
  } else {
    for (const s of state.available) {
      const shown = (state.loaded || []).indexOf(s.name) >= 0;
      const li = document.createElement('li');
      if (shown) li.className = 'active';
      li.innerHTML =
        '<button ' + (shown ? 'disabled' : '') + ' data-load=""' + s.name + '"">' +
        (shown ? 'shown' : 'show') + '</button>' +
        '<span class=""name"">' + s.name + '</span>' +
        '<span class=""meta"">' + (s.splats ? s.splats.toLocaleString() + ' splats' : '') + '</span>';
      list.appendChild(li);
    }
  }

  const tbl = document.getElementById('bindings');
  const keys = Object.keys(state.bindings || {});
  tbl.innerHTML = keys.length
    ? keys.map(k =>
        '<tr><td>' + k + '</td><td>' + state.bindings[k].join(', ') + '</td>' +
        '<td style=""width:1%""><button data-unbind=""' + k.replace(/""/g, '&quot;') + '"">remove</button></td></tr>'
      ).join('')
    : '<tr><td class=""empty"">none</td></tr>';

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
