import type { SetupState } from './types'

/**
 * The transport for the companion window.
 *
 * The setup page runs before the game does, so there is no plugin and no HTTP server to
 * talk to - the host is the Tauri app around the webview, reached over its invoke channel.
 * Commands are fire-and-forget: every one of them ends with the host pushing fresh state,
 * so there is nothing to correlate a reply against.
 *
 * There was a second transport here, chrome.webview.postMessage, for the C# app that used
 * to be the Windows half. That app is gone and both platforms are Tauri now.
 */
type Push =
  | ({ type: 'state' } & SetupState)
  | { type: 'log'; line: string }
  // Progress on its own: rebuilding the whole state a hundred times during a download
  // would mean walking every capture on disk for each percent.
  | { type: 'progress'; percent: number | null }
  // The word before the work: building a whole state means walking the disk, and the
  // button should not look dead while that happens.
  | { type: 'busy'; what: string | null }
  // The game starting is one flag, and the host watches for it rather than waiting to be
  // asked - nobody presses refresh to tell the app they quit VelociDrone.
  | { type: 'running'; running: boolean }
  // The .ply someone just chose, before anything is copied. The page asks for the
  // track's name on this, and only `addTrack {path, name}` writes anything.
  | { type: 'picked'; path: string; stem: string }

type TauriGlobal = {
  core: { invoke: (cmd: string, args?: Record<string, unknown>) => Promise<unknown> }
  event: {
    listen: (name: string, fn: (e: { payload: Push }) => void) => Promise<() => void>
  }
}

const tauri: TauriGlobal | undefined = (window as unknown as { __TAURI__?: TauriGlobal }).__TAURI__

export const hosted = !!tauri

export type Command =
  | 'refresh'
  | 'pick'
  | 'installMod'
  | 'uninstallMod'
  | 'pickPly'
  | 'removeTrack'
  | 'removeCapture'
  | 'unbindTrack'
  | 'refreshCatalog'
  | 'get'
  | 'addTrack'
  | 'fly'

/**
 * Registering a Tauri listener is asynchronous, and the page's first command goes out in
 * the same breath as its subscription. Sent straight away, that command is answered
 * before anything is listening and the reply is dropped - which is a window that never
 * fills in, with nothing to say why, because a state is only pushed when asked for.
 */
let listening: Promise<unknown> | null = null

// Widened to a third value for addTrack, which needs a path and a name together - one
// id string was never going to carry two values.
export function send(cmd: Command, id?: string, arg?: Record<string, unknown>): void {
  if (!tauri) return devSend(cmd, id)
  const invoke = () =>
    tauri.core.invoke('dispatch', { cmd, id: id ?? null, arg: arg ?? null })
  void (listening ? listening.then(invoke, invoke) : invoke())
}

export function subscribe(fn: (m: Push) => void): () => void {
  if (!tauri) return devSubscribe(fn)
  let off: (() => void) | null = null
  let gone = false
  const ready = tauri.event.listen('push', (e) => fn(e.payload)).then((u) => {
    if (gone) u()
    else off = u
  })
  listening = ready
  void ready
  return () => {
    gone = true
    off?.()
  }
}

// ---------------------------------------------------------------- dev stand-in
//
// Opened in a plain browser there is no host, and a page that renders nothing cannot be
// worked on. This is the same idea as vite-mock-api.ts for the control UI: enough state
// to lay the page out, and it never reaches a build the app ships (`bun run dev` only).

let devListeners: ((m: Push) => void)[] = []

const devState: SetupState = {
  game: 'C:\\Users\\a\\Downloads\\Velocidrone Windows Launcher\\app',
  mod: '0.1.0.0',
  bundledMod: '0.1.0.0',
  missing: [],
  ready: true,
  running: false,
  busy: null,
  busyPercent: null,
  launchArgs: '-force-d3d12',
  // Real state carries this unconditionally too - state.rs asks the OS for a LAN-facing
  // address regardless of whether the game is running, so setting it here regardless of
  // `running` below is not a deviation from production, it is what production does.
  // Whether anything is actually listening at it is a separate question the shell gates
  // on `running` - this stand-in exists so the field has something to lay out, not so
  // its presence alone means the address is live.
  lanUrl: 'http://192.168.1.42:8777/',
  tracks: [
    {
      track: 'VDGS FDF',
      capture: 'FDF-2026-08-24',
      splats: 1497617,
      bytes: 128_800_000,
      collision: true,
      captureInstalled: true,
      converted: true,
      inGame: true,
      fromServer: false,
    },
    {
      track: 'VDGS Playroom',
      capture: 'playroom',
      splats: 1916379,
      bytes: 156_500_000,
      collision: true,
      captureInstalled: true,
      converted: true,
      inGame: true,
      fromServer: false,
    },
    {
      track: 'VDGS Nelson',
      capture: 'nelson-lod2',
      splats: 0,
      collision: false,
      captureInstalled: false,
      converted: false,
      inGame: true,
      fromServer: false,
    },
  ],
  unbound: [
    { name: 'drjohnson-high', splats: 3177554, collision: true, bytes: 260_100_000 },
    { name: 'testcube', splats: 640, collision: false, bytes: 52_000 },
  ],
  catalog: {
    url: 'https://vdgs.saqoo.sh/catalog.json',
    error: null,
    entries: [
      {
        id: 'fdf-2026-08-24',
        name: 'FDF',
        description: 'An FPV practice field in Japan, flown from inside.',
        author: 'Saqoosha',
        licence: 'CC0-1.0',
        splats: 1497617,
        bytes: 123_657_212,
        installed: true,
        update: true,
        installAs: 'FDF-2026-08-24',
      },
      {
        id: 'jdl-2026-r5',
        name: 'JDL 2026 R5',
        description: 'A Japan Drone League round, shot from the ground and the air.',
        author: 'Saqoosha',
        licence: 'CC0-1.0',
        splats: 3_900_000,
        bytes: 402_000_000,
        installed: false,
        update: false,
        installAs: 'JDL-2026-R5',
      },
    ],
  },
  // Off in the stand-in so the warning does not cover the Fly button while laying out.
  trueLens: false,
}

function devPush(m: Push) {
  for (const fn of devListeners) fn(m)
}

function devSend(cmd: Command, id?: string) {
  if (!import.meta.env.DEV) return
  if (cmd === 'refresh') {
    devPush({ type: 'state', ...devState })
    return
  }
  // The stand-in for the file dialog: a fixed path back, so the name dialog can be laid out
  // in a browser where no host exists to open a real one.
  if (cmd === 'pickPly') {
    devPush({ type: 'picked', path: '/Users/you/Downloads/himeji-lod2.ply', stem: 'himeji-lod2' })
    return
  }
  devPush({
    type: 'log',
    line: new Date().toTimeString().slice(0, 8) + '  ' + cmd + (id ? ' ' + id : ''),
  })
  devPush({ type: 'state', ...devState })
}

function devSubscribe(fn: (m: Push) => void): () => void {
  devListeners.push(fn)
  return () => {
    devListeners = devListeners.filter((f) => f !== fn)
  }
}
