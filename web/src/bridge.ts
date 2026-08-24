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
  | 'installCapture'
  | 'addTrack'
  | 'fly'

export function send(cmd: Command): void {
  if (host) host.postMessage({ cmd })
  else devSend(cmd)
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
    },
    {
      track: 'VDGS Nelson',
      capture: 'nelson-lod2',
      splats: 0,
      collision: false,
      captureInstalled: false,
      converted: false,
      inGame: true,
    },
  ],
  unbound: [
    { name: 'drjohnson-high', splats: 3177554, collision: true, bytes: 260_100_000 },
    { name: 'testcube', splats: 640, collision: false, bytes: 52_000 },
  ],
}

function devPush(m: Push) {
  for (const fn of devListeners) fn(m)
}

function devSend(cmd: Command) {
  if (!import.meta.env.DEV) return
  if (cmd === 'refresh') {
    devPush({ type: 'state', ...devState })
    return
  }
  devPush({ type: 'log', line: new Date().toTimeString().slice(0, 8) + '  ' + cmd })
  devPush({ type: 'state', ...devState })
}

function devSubscribe(fn: (m: Push) => void): () => void {
  devListeners.push(fn)
  return () => {
    devListeners = devListeners.filter((f) => f !== fn)
  }
}
