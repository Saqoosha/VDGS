// FDF CUP 2026 R6 A-main semi-final: three pilots' DVR paths on the 3DGS scan, played back together.
// race.json (built from each flight's poses60_pad.json): the paths at the clip's 30 fps, the start of the race in clip
// time (from KANATA's official total; SENA and SAQOOSHA then agree to 15 and 29 ms), the finish gate and the laps.
import * as pc from 'playcanvas'
import { bakeSky } from './sky'

type Pilot = { name: string; color: string; video: string; end: number; crashed: boolean; finish: number | null; lap_ends: number[]; laps: number[]; total: number
  official: { pos: number; laps: number; time: string; gap: string }; pos: number[][] }
type Race = { title: string; fps: number; start: number; scene: string; laps: number; gate: { centre: number[] }; pilots: Pilot[] }

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T
const QS = new URLSearchParams(location.search)
const DATA = import.meta.env.BASE_URL + (QS.get('data') ?? import.meta.env.VITE_DEFAULT_DATA ?? 'race-sf-semifinal') + '/'
const TRAIL = 3                                                    // seconds of trail behind each drone
const canvas = $<HTMLCanvasElement>('c'), status = $('status')
const app = new pc.Application(canvas, { mouse: new pc.Mouse(canvas), graphicsDeviceOptions: { antialias: true } })
app.setCanvasFillMode(pc.FILLMODE_FILL_WINDOW); app.setCanvasResolution(pc.RESOLUTION_AUTO)
window.addEventListener('resize', () => app.resizeCanvas())
const cam = new pc.Entity('cam'); cam.addComponent('camera', { clearColor: new pc.Color(0.91, 0.92, 0.94), fov: 50, nearClip: 0.2, farClip: 2000 })
app.root.addChild(cam)

// sky and scan
const skyImg = new Image(); skyImg.onload = () => { app.scene.skyboxMip = 0; app.scene.skybox = bakeSky(app.graphicsDevice, skyImg) }; skyImg.src = DATA + 'sky.jpg'
function loadScene(name: string) {
  const a = new pc.Asset(name, 'gsplat', { url: `${DATA}scene/${name}.sog` }); app.assets.add(a)
  a.on('load', () => { const e = new pc.Entity('scan'); e.addComponent('gsplat', { asset: a }); app.root.addChild(e); status.hidden = true })
  a.on('error', (err: string) => { status.textContent = 'scene failed: ' + err })
  app.assets.load(a)
}

const race: Race = await fetch(DATA + 'race.json').then(r => r.json())
$('title').textContent = race.title.replace('FDF CUP 2026 R6 · ', '')
loadScene(race.scene)
const P = race.pilots, FPS = race.fps
const T_END = Math.max(...P.map(p => p.end))                      // clip time of the last solved frame of anyone
const T0 = Math.max(0, race.start - 2)                            // playback opens two seconds before the start
// position at clip time t: frames interpolated; before the path starts, its first frame; after its end, its last
function at(p: Pilot, t: number): pc.Vec3 {
  const f = Math.max(0, Math.min(p.pos.length - 1, t * FPS)), i = Math.floor(f), j = Math.min(p.pos.length - 1, i + 1), u = f - i
  const a = p.pos[i], b = p.pos[j]; return new pc.Vec3(a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u)
}
const hex = (h: string) => new pc.Color(parseInt(h.slice(1, 3), 16) / 255, parseInt(h.slice(3, 5), 16) / 255, parseInt(h.slice(5, 7), 16) / 255)
const grey = new pc.Color(0.55, 0.57, 0.6), WHITE = new pc.Color(1, 1, 1)
const drones = P.map(p => {
  const m = new pc.StandardMaterial(); m.diffuse = new pc.Color(0, 0, 0); m.emissive = hex(p.color); m.useLighting = false; m.update()
  const e = new pc.Entity(p.name); e.addComponent('render', { type: 'sphere', material: m, castShadows: false }); e.setLocalScale(0.7, 0.7, 0.7)
  app.root.addChild(e)
  const tag = document.createElement('div'); tag.className = 'tag'; tag.style.background = p.color; document.body.appendChild(tag)
  return { p, e, m, tag, col: hex(p.color) }
})

// the pilots' own DVR feeds (the broadcast's quad view, one quadrant each, 4:3), played on the same clock
const videos = P.map(p => {
  const tile = document.createElement('div'); tile.className = 'tile'; tile.style.borderTopColor = p.color
  const v = document.createElement('video'); v.src = DATA + p.video; v.muted = true; v.playsInline = true; v.preload = 'auto'
  const name = document.createElement('span'); name.textContent = p.name; name.style.background = p.color
  tile.append(v, name); $('dvr').appendChild(tile); return v
})
function syncVideos() {
  for (const v of videos) {
    if (playing && !seeking) {
      v.playbackRate = speed; if (v.paused) v.play().catch(() => {})
      if (Math.abs(v.currentTime - t) > 0.15 && !v.seeking) v.currentTime = t
    } else {
      if (!v.paused) v.pause()
      if (Math.abs(v.currentTime - t) > 0.02 && !v.seeking) v.currentTime = t
    }
  }
}

// timeline state
let t = T0, playing = false, speed = 1
const play = $<HTMLButtonElement>('play')
const setPlaying = (v: boolean) => { playing = v; play.textContent = v ? '❚❚ Pause' : '▶ Play' }
play.onclick = () => { if (t >= T_END - 0.01) t = T0; setPlaying(!playing) }
$<HTMLSelectElement>('speed').onchange = e => { speed = Number((e.target as HTMLSelectElement).value) }
window.addEventListener('keydown', e => { if (e.key === ' ') { play.click(); e.preventDefault() }
  if (e.key === 'ArrowRight') t = Math.min(T_END, t + (e.shiftKey ? 5 : 1)); if (e.key === 'ArrowLeft') t = Math.max(T0, t - (e.shiftKey ? 5 : 1)) })
const raceTime = (tc: number) => tc - race.start

// live standings: finished first by finishing time, then by laps done, then who crossed the line earlier
function standings(tc: number) {
  return P.map(p => {
    const done = p.lap_ends.filter(x => x <= tc).length, finished = p.finish !== null && tc >= p.finish
    const out = tc > p.end && !finished
    return { p, done, finished, out, last: done ? p.lap_ends[done - 1] : -1 }
  }).sort((a, b) => (Number(b.finished) - Number(a.finished)) || (b.done - a.done) || (a.last - b.last))
}
const fmt = (s: number) => s >= 60 ? `${Math.floor(s / 60)}:${(s % 60).toFixed(3).padStart(6, '0')}` : s.toFixed(3)
function drawBoard(tc: number) {
  const rt = raceTime(tc)
  $('rows').replaceChildren(...standings(tc).map((s, k) => {
    const tr = document.createElement('tr'); if (s.out) tr.className = 'out'
    const cur = s.finished ? 'FINISH' : s.out ? 'OUT' : rt < 0 ? 'START' : `LAP ${Math.min(s.done + 1, race.laps)}/${race.laps}`
    const time = s.finished ? fmt(s.p.total) : s.done ? fmt(s.last - race.start) : rt < 0 ? '' : fmt(Math.min(rt, s.p.end - race.start))
    const sub = s.done && !s.finished ? `${s.out ? `${s.done}L · ` : ''}lap ${s.done} ${s.p.laps[s.done - 1].toFixed(3)}` : s.finished ? `${s.p.laps.length}L` : ''
    const c = (cls: string, text: string) => { const td = document.createElement('td'); td.className = cls; td.textContent = text; return td }
    const name = document.createElement('td'); name.className = 'name'; const dot = document.createElement('i'); dot.style.background = s.out ? '#8c9199' : s.p.color
    name.append(dot, document.createTextNode(s.p.name))
    const tm = document.createElement('td'); tm.className = 'time'; tm.textContent = time; if (sub) { const sm = document.createElement('small'); sm.textContent = sub; tm.appendChild(sm) }
    tr.append(c('pos', String(k + 1)), name, c('lap', cur), tm); return tr
  }))
  $('laps').replaceChildren(...P.map(p => {
    const d = document.createElement('div'); const b = document.createElement('b'); b.textContent = p.name; b.style.color = p.color
    const done = p.lap_ends.filter(x => x <= tc).length
    d.append(b, document.createTextNode(p.laps.slice(0, done).map(x => x.toFixed(3)).join('  ·  ') || '—')); return d
  }))
  const unit = document.createElement('small'); unit.textContent = 's'
  $('clock').replaceChildren(document.createTextNode(rt < 0 ? '−' + (-rt).toFixed(3) : rt.toFixed(3)), unit)
}
$('orows').replaceChildren(...[...P].sort((a, b) => a.official.pos - b.official.pos).map(p => {
  const tr = document.createElement('tr')
  for (const [txt, st] of [[String(p.official.pos), ''], [p.name, `color:${p.color}`], [`${p.official.laps}L ${p.official.time}`, ''], [p.official.gap, 'color:#6b7180']]) {
    const td = document.createElement('td'); td.textContent = txt; if (st) td.setAttribute('style', st); tr.appendChild(td) }
  return tr
}))

// timeline canvas: one row per pilot with its lap ends, the playhead; drag to seek
const tl = $<HTMLCanvasElement>('tl')
function drawTimeline(tc: number) {
  const r = tl.getBoundingClientRect(), dpr = devicePixelRatio, W = Math.round(r.width * dpr), H = Math.round(r.height * dpr)
  if (tl.width !== W || tl.height !== H) { tl.width = W; tl.height = H }
  const g = tl.getContext('2d')!; g.clearRect(0, 0, W, H)
  const L = 96 * dpr, R = W - 8 * dpr, x = (tt: number) => L + (tt - T0) / (T_END - T0) * (R - L), rowH = H / P.length
  g.font = `600 ${12 * dpr}px Inter, sans-serif`; g.textBaseline = 'middle'
  P.forEach((p, k) => {
    const y = rowH * (k + 0.5)
    g.fillStyle = '#16181d'; g.fillText(p.name, 0, y)
    g.fillStyle = '#e3e5ea'; g.fillRect(L, y - 3 * dpr, R - L, 6 * dpr)
    g.fillStyle = p.color; g.globalAlpha = 0.35; g.fillRect(x(race.start), y - 3 * dpr, x(Math.min(p.end, T_END)) - x(race.start), 6 * dpr); g.globalAlpha = 1
    for (const [n, e] of p.lap_ends.entries()) {
      g.fillStyle = p.color; g.fillRect(x(e) - 1.5 * dpr, y - 9 * dpr, 3 * dpr, 18 * dpr)
      g.font = `600 ${11 * dpr}px "Barlow Condensed", sans-serif`; g.fillStyle = '#16181d'; g.fillText(`L${n + 1} ${p.laps[n].toFixed(2)}`, x(e) + 4 * dpr, y - 9 * dpr)
      g.font = `600 ${12 * dpr}px Inter, sans-serif`
    }
    if (p.crashed) { g.fillStyle = '#6b7180'; g.fillText('✕', x(p.end) - 4 * dpr, y) }
  })
  g.strokeStyle = '#16181d'; g.lineWidth = 1 * dpr; g.setLineDash([4 * dpr, 4 * dpr]); g.beginPath(); g.moveTo(x(race.start), 0); g.lineTo(x(race.start), H); g.stroke(); g.setLineDash([])
  g.fillStyle = '#16181d'; g.fillRect(x(tc) - 1 * dpr, 0, 2 * dpr, H)
}
let seeking = false
const seek = (e: PointerEvent) => { const r = tl.getBoundingClientRect(); const L = 96, R = r.width - 8
  t = Math.max(T0, Math.min(T_END, T0 + (e.clientX - r.left - L) / (R - L) * (T_END - T0))) }
tl.addEventListener('pointerdown', e => { seeking = true; tl.setPointerCapture(e.pointerId); seek(e) })
tl.addEventListener('pointermove', e => { if (seeking) seek(e) })
tl.addEventListener('pointerup', () => { seeking = false })

// camera: orbit the drones' middle, always far enough to hold all three; drag turns it, the wheel zooms
const course = (() => { const all = P.flatMap(p => p.pos); const c = [0, 1, 2].map(k => all.reduce((s, q) => s + q[k], 0) / all.length); return new pc.Vec3(c[0], c[1], c[2]) })()
const view = { yaw: 30, pitch: -32, zoom: 1, target: course.clone(), dist: 60 }
let drag: { x: number; y: number } | null = null
canvas.addEventListener('pointerdown', e => { drag = { x: e.clientX, y: e.clientY }; canvas.setPointerCapture(e.pointerId) })
canvas.addEventListener('pointerup', () => { drag = null })
canvas.addEventListener('pointermove', e => { if (!drag) return; view.yaw -= (e.clientX - drag.x) * 0.3; view.pitch = Math.max(-85, Math.min(-8, view.pitch - (e.clientY - drag.y) * 0.3)); drag = { x: e.clientX, y: e.clientY } })
canvas.addEventListener('wheel', e => { view.zoom = Math.max(0.3, Math.min(3, view.zoom * Math.exp(e.deltaY * 0.001))); e.preventDefault() }, { passive: false })
const auto = $<HTMLInputElement>('auto')

const tmpA = new pc.Vec3(), tmpB = new pc.Vec3()
app.on('update', (dt: number) => {
  if (playing && !seeking) { t += dt * speed; if (t >= T_END) { t = T_END; setPlaying(false) } }
  // drones, trails, tags
  const lines: pc.Vec3[] = [], cols: pc.Color[] = []
  for (const d of drones) {
    const tc = Math.min(t, d.p.end), now = at(d.p, tc), gone = t > d.p.end
    d.e.setPosition(now); d.m.emissive = gone ? grey : d.col; d.m.update()
    const n = Math.round(TRAIL * FPS)
    for (let k = 0; k < n; k++) {
      const t1 = tc - k / FPS, t2 = tc - (k + 1) / FPS; if (t2 < 0) break
      const f = 1 - k / n, c = gone ? grey : new pc.Color().lerp(d.col, WHITE, 1 - f)
      lines.push(at(d.p, t1), at(d.p, t2)); cols.push(c, c)
    }
    const s = cam.camera!.worldToScreen(now, tmpA), behind = cam.forward.dot(tmpB.copy(now).sub(cam.getPosition())) < 0
    d.tag.style.display = behind ? 'none' : ''; d.tag.style.left = s.x + 'px'; d.tag.style.top = s.y + 'px'; d.tag.style.background = gone ? '#8c9199' : d.p.color
    const done = d.p.lap_ends.filter(x => x <= t).length
    d.tag.textContent = d.p.name; const sm = document.createElement('small')
    sm.textContent = d.p.finish !== null && t >= d.p.finish ? 'FINISH' : gone ? 'OUT' : raceTime(t) < 0 ? '' : `L${Math.min(done + 1, race.laps)}`
    d.tag.appendChild(sm)
  }
  if (lines.length) app.drawLines(lines, cols, false)
  // camera
  const pts = drones.map(d => d.e.getPosition())                   // all three, landed or crashed ones too: the view always holds them
  const mid = pts.reduce((s, q) => s.add(q), new pc.Vec3()).mulScalar(1 / pts.length)
  const spread = Math.max(8, ...pts.map(q => q.distance(mid)))
  const fov = cam.camera!.fov * Math.PI / 180, aspect = app.graphicsDevice.width / app.graphicsDevice.height
  const need = (spread * 1.35) / Math.tan(Math.min(fov, 2 * Math.atan(Math.tan(fov / 2) * aspect)) / 2)
  const k = 1 - Math.exp(-dt * 1.5)
  view.target.lerp(view.target, mid, k); view.dist += (Math.max(25, need) - view.dist) * k
  if (auto.checked && playing) view.yaw += dt * speed * 6                // a slow turn, 6 degrees a second of race
  const r = new pc.Quat().setFromEulerAngles(view.pitch, view.yaw, 0)
  const back = r.transformVector(new pc.Vec3(0, 0, view.dist * view.zoom), new pc.Vec3())
  cam.setPosition(back.add(view.target)); cam.setRotation(r)
  drawBoard(t); drawTimeline(t); syncVideos()
})
app.start()
