import type { SetupState } from './types'

/**
 * The transport for the companion window.
 *
 * The setup page runs before the game does, so there is no plugin and no HTTP server to
 * talk to - the host is the C# app around the WebView, reached over
 * chrome.webview.postMessage. Commands are fire-and-forget: every one of them ends with
 * the host pushing fresh state, so there is nothing to correlate a reply against.
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

type WebViewHost = {
  postMessage: (message: unknown) => void
  addEventListener: (type: 'message', fn: (e: { data: Push }) => void) => void
  removeEventListener: (type: 'message', fn: (e: { data: Push }) => void) => void
}

const host: WebViewHost | undefined = (
  window as unknown as { chrome?: { webview?: WebViewHost } }
).chrome?.webview

export const hosted = !!host

export type Command =
  | 'refresh'
  | 'pick'
  | 'installMod'
  | 'uninstallMod'
  | 'removeTrack'
  | 'refreshCatalog'
  | 'get'
  | 'installCapture'
  | 'addTrack'
  | 'fly'

export function send(cmd: Command, id?: string): void {
  if (host) host.postMessage({ cmd, id })
  else devSend(cmd, id)
}

export function subscribe(fn: (m: Push) => void): () => void {
  if (!host) return devSubscribe(fn)
  const listener = (e: { data: Push }) => fn(e.data)
  host.addEventListener('message', listener)
  return () => host.removeEventListener('message', listener)
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
      },
    ],
  },
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
