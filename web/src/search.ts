import type { Scene } from './types'

/**
 * Case-insensitive substring match against a `name` field, shared by every list in this
 * app that a person searches by typing - scenes here, the merged track table in
 * Tracks.tsx. One matcher, so "search" means the same thing everywhere it appears.
 */
export function filterByName<T extends { name: string }>(items: T[], q: string): T[] {
  const n = q.trim().toLowerCase()
  if (!n) return items
  return items.filter((item) => item.name.toLowerCase().includes(n))
}

export function filterScenes(scenes: Scene[], q: string): Scene[] {
  return filterByName(scenes, q)
}
