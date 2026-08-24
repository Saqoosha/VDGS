import { describe, expect, it } from 'vitest'
import { filterScenes } from './search'
import type { Scene } from './types'

function scene(name: string): Scene {
  return {
    name,
    source: 'local',
    kind: 'converted',
    splats: 1,
    hasCollision: false,
    shown: false,
    scale: 1,
    y: 0,
    backdrop: false,
    collision: false,
    collisionView: 'off',
  }
}

describe('filterScenes', () => {
  const all = [scene('playroom'), scene('drjohnson-high'), scene('luigi')]

  it('empty query returns all', () => {
    expect(filterScenes(all, '  ')).toEqual(all)
  })

  it('matches case-insensitively', () => {
    expect(filterScenes(all, 'Play').map((s) => s.name)).toEqual(['playroom'])
  })

  it('returns nothing when nothing matches', () => {
    expect(filterScenes(all, 'zzz')).toEqual([])
  })
})
