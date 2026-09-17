import type { CollisionView, Scene, Status } from './types'

/**
 * Two transports, the same reason bridge.ts has two.
 *
 * Served by the plugin, the page is same-origin and a relative fetch is right. Inside the
 * companion the page is on Tauri's own origin, so the same fetch would be cross-origin -
 * and answering it would mean putting Access-Control-Allow-Origin on a server that is
 * open to the whole LAN. Going out through the host instead never touches the webview,
 * so the plugin's headers stay exactly as they are.
 */
type HostHttp = { fetch: (url: string, init?: RequestInit) => Promise<Response> }
const host: HostHttp | undefined = (
  window as unknown as { __TAURI__?: { http?: HostHttp } }
).__TAURI__?.http

const base = host ? 'http://127.0.0.1:8777' : ''
const call = (url: string, init?: RequestInit): Promise<Response> =>
  host ? host.fetch(base + url, init) : fetch(base + url, init)

async function post(url: string, body: object = {}): Promise<void> {
  const r = await call(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!r.ok) {
    let msg = r.statusText
    try {
      const j = (await r.json()) as { error?: string }
      if (j && j.error) msg = String(j.error)
    } catch {
      /* keep statusText */
    }
    throw new Error(msg)
  }
}

export async function getStatus(): Promise<Status> {
  const r = await call('/api/status', { cache: 'no-store' })
  if (!r.ok) throw new Error('status ' + r.status)
  return r.json() as Promise<Status>
}

export const load = (splat: string) => post('/api/load', { splat })
export const unload = () => post('/api/unload', {})
export const bind = (splats: string[]) => post('/api/bind', { splats })
export const unbind = (track?: string) =>
  post('/api/unbind', track ? { track } : {})
export const setBackdrop = (splat: string, on: boolean) =>
  post('/api/backdrop', { splat, on })
export const setCollision = (splat: string, on: boolean) =>
  post('/api/collision', { splat, on })
export const setCollisionView = (splat: string, mode: CollisionView) =>
  post('/api/collisionview', { splat, mode })
export const setTransform = (
  splat: string,
  v: { scale?: number; y?: number; x?: number; z?: number },
) => {
  const body: Record<string, unknown> = { splat }
  if (v.scale != null) body.scale = v.scale
  if (v.y != null) body.y = v.y
  if (v.x != null) body.x = v.x
  if (v.z != null) body.z = v.z
  return post('/api/transform', body)
}

export const setOrientation = (
  splat: string,
  v: { up?: string; turn?: number; mirror?: boolean },
) => {
  const body: Record<string, unknown> = { splat }
  if (v.up != null) body.up = v.up
  if (v.turn != null) body.turn = v.turn
  if (v.mirror != null) body.mirror = v.mirror
  return post('/api/transform', body)
}

export const setLod = (
  splat: string,
  v: { lodDetail?: number; lodBudget?: number },
) => {
  const body: Record<string, unknown> = { splat }
  if (v.lodDetail != null) body.lodDetail = v.lodDetail
  if (v.lodBudget != null) body.lodBudget = v.lodBudget
  return post('/api/transform', body)
}

export type { Scene, Status }
