import type { CollisionView, Scene, Status } from './types'

async function post(url: string, body: object = {}): Promise<void> {
  const r = await fetch(url, {
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
  const r = await fetch('/api/status', { cache: 'no-store' })
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
export const setTransform = (splat: string, scale?: number, y?: number) => {
  const body: Record<string, unknown> = { splat }
  if (scale != null) body.scale = scale
  if (y != null) body.y = y
  return post('/api/transform', body)
}

export type { Scene, Status }
