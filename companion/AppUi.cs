namespace VDGSCompanion
{
    /// <summary>
    /// The window's markup, embedded so the app stays a single exe beside its DLLs.
    ///
    /// It is deliberately the same design as the mod's in-game browser UI (src/VDGS/WebUi.cs):
    /// the two are the same tool at different moments - one sets the game up, the other
    /// drives it while flying - and they should not look like different products.
    ///
    /// No framework, no build step, no external requests. The page talks to the host over
    /// chrome.webview.postMessage; see MainForm.
    /// </summary>
    internal static class AppUi
    {
        internal const string Html = @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>VDGS Companion</title>
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
    --bad: #b42318;
  }
  * { box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    margin: 0; padding: 24px;
    background: var(--bg); color: var(--text);
    font: 15px/1.5 -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
    user-select: none; cursor: default;
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
  .path { font-size: 14px; word-break: break-all; }
  .path.none { color: var(--muted); }
  ul { list-style: none; margin: 0; padding: 0; }
  li { display: flex; align-items: center; gap: 12px; padding: 10px 12px;
       border: 1px solid var(--line); border-radius: 8px; margin-bottom: 8px; }
  li .name { font-weight: 600; }
  li .meta { color: var(--muted); font-size: 13px; margin-left: auto;
             font-variant-numeric: tabular-nums; }
  button { font: inherit; padding: 7px 14px; border-radius: 7px; cursor: pointer;
           border: 1px solid var(--line); background: #fff; color: var(--text); }
  button:hover { border-color: #c3c9d2; }
  button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
  button.primary:hover { background: #1d4fd8; }
  button:disabled { opacity: .45; cursor: default; }
  button.big { padding: 11px 18px; font-weight: 600; }
  .row { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
  .row.spread { justify-content: space-between; }
  .hint { color: var(--muted); font-size: 13px; margin-top: 10px; }
  .empty { color: var(--muted); padding: 6px 0; }
  .state { margin-top: 10px; font-size: 14px; }
  .state.ok { color: var(--ok); }
  .state.bad { color: var(--bad); }
  .state b { font-weight: 600; }
  pre#log { margin: 0; font: 12px/1.6 ui-monospace, SFMono-Regular, Consolas, monospace;
            color: var(--muted); white-space: pre-wrap; word-break: break-word;
            max-height: 150px; overflow-y: auto; user-select: text; cursor: text; }
</style>
</head>
<body>
<div class=""wrap"">
  <h1><span class=""dot"" id=""dot""></span>VDGS Companion</h1>

  <div class=""card"">
    <div class=""row spread"">
      <div style=""flex:1; min-width:0"">
        <div class=""label"">VelociDrone</div>
        <div class=""path none"" id=""path"">looking…</div>
      </div>
      <button id=""pick"">Change…</button>
    </div>
    <div class=""state"" id=""state""></div>
  </div>

  <div class=""card"">
    <div class=""label"">Captures installed</div>
    <ul id=""scenes""><li class=""empty"">none yet</li></ul>
    <div class=""row"" style=""margin-top:14px"">
      <button id=""installMod"">Install mod (.zip)…</button>
      <button id=""installScene"">Install capture (.zip)…</button>
      <button id=""addTrack"">Add track…</button>
    </div>
    <div class=""hint"" id=""hint""></div>
  </div>

  <div class=""card"">
    <div class=""row spread"">
      <div>
        <div class=""label"">Ready</div>
        <div id=""flyNote"" class=""path none"">-</div>
      </div>
      <button class=""primary big"" id=""fly"">Fly</button>
    </div>
  </div>

  <div class=""card"">
    <div class=""label"">Log</div>
    <pre id=""log""></pre>
  </div>
</div>

<script>
const host = window.chrome && window.chrome.webview;
const $ = id => document.getElementById(id);
const send = cmd => host && host.postMessage({ cmd });

// Track names and scene names both come from disk, so nothing here is ever built with
// innerHTML - a capture folder can be called whatever its author felt like.
function el(tag, cls, text) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined) n.textContent = text;
  return n;
}

function render(s) {
  $('dot').className = 'dot' + (s.ready ? ' live' : '');

  $('path').textContent = s.game || 'not found';
  $('path').className = 'path' + (s.game ? '' : ' none');

  const st = $('state');
  st.textContent = '';
  if (!s.game) {
    st.className = 'state bad';
    st.textContent = 'Use Change… to point at the folder that holds velocidrone.exe.';
  } else if (s.missing.length) {
    st.className = 'state bad';
    st.textContent = 'Missing: ' + s.missing.join(', ');
    if (s.missing.indexOf('BepInEx') >= 0)
      st.appendChild(el('div', null, 'BepInEx 5.4.23.5 (win_x64) has to be unzipped into the game folder first.'));
  } else {
    st.className = 'state ok';
    st.textContent = 'Mod ' + s.mod + ' installed.';
  }

  const list = $('scenes');
  list.textContent = '';
  if (!s.scenes.length) {
    list.appendChild(el('li', 'empty', s.game ? 'none yet - install a capture below' : ''));
  } else {
    for (const sc of s.scenes) {
      const li = el('li');
      li.appendChild(el('span', 'name', sc.name));
      li.appendChild(el('span', 'meta', sc.splats + ' splats' + (sc.collision ? '' : '  ·  no collision')));
      list.appendChild(li);
    }
  }

  $('hint').textContent = s.running
    ? 'VelociDrone is running. Close it before installing anything - it holds its files open.'
    : '';

  const note = $('flyNote');
  note.className = 'path' + (s.ready ? '' : ' none');
  note.textContent = s.running ? 'VelociDrone is already running'
    : s.ready ? 'Starts the game with ' + s.launchArgs + ', which the captures need in order to draw at all.'
    : 'Install the mod first.';

  $('fly').disabled = !s.game || s.running;
  for (const id of ['installMod', 'installScene', 'addTrack'])
    $(id).disabled = !s.game || s.running;
}

function log(line) {
  const p = $('log');
  p.textContent += line + '\n';
  p.scrollTop = p.scrollHeight;
}

if (host) {
  host.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'state') render(m);
    else if (m.type === 'log') log(m.line);
  });
}

for (const id of ['pick', 'installMod', 'installScene', 'addTrack', 'fly'])
  $(id).addEventListener('click', () => send(id));

// The window has no browser chrome, so the shortcuts that would open a devtools panel or
// navigate away have nowhere useful to go.
document.addEventListener('contextmenu', e => e.preventDefault());

send('refresh');
</script>
</body>
</html>
";
    }
}
