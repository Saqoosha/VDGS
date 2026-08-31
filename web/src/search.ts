import type { Scene } from './types'

export function filterScenes(scenes: Scene[], q: string): Scene[] {
  const n = q.trim().toLowerCase()
  if (!n) return scenes
  return scenes.filter((s) => s.name.toLowerCase().includes(n))
}
