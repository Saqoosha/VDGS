import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import Control from './Control'
import type { Scene, Status } from '../types'

const xss = '<img src=x onerror=alert(1)>'

function scene(over: Partial<Scene> = {}): Scene {
  return {
    name: 'my-house',
    source: 'local',
    kind: 'converted',
    splats: 100,
    hasCollision: false,
    shown: true,
    scale: 1,
    y: 0,
    x: 0,
    z: 0,
    up: '+y',
    turn: 0,
    mirror: false,
    backdrop: false,
    collision: false,
    collisionView: 'off',
    ...over,
  }
}

function sample(s: Scene): Status {
  return {
    track: 'VDGS my-house',
    loaded: [s.name],
    available: [s],
    bindings: {},
  }
}

let current: Status | null = null
vi.mock('../useStatus', () => ({
  useStatus: () => ({ state: current, live: true, refresh: async () => {} }),
}))

describe('Control XSS', () => {
  it('renders a hostile capture name as text, not HTML', () => {
    current = sample(scene({ name: xss }))
    render(<Control />)
    expect(screen.queryByRole('img')).toBeNull()
    expect(screen.getAllByText(xss).length).toBeGreaterThan(0)
  })
})

// The three things earlier reviews parked for this task - each is a control that would
// otherwise look broken with no explanation.
describe('the parked items', () => {
  it('hides Mirror for a converted capture - the flip happens while a .ply is parsed', () => {
    current = sample(scene({ kind: 'converted' }))
    render(<Control />)
    expect(screen.queryByText(/mirror/i)).toBeNull()
  })

  it('shows Mirror for a .ply capture', () => {
    current = sample(scene({ kind: 'ply' }))
    render(<Control />)
    expect(screen.getByText(/mirror/i)).toBeInTheDocument()
  })

  it('disables the backdrop box and says why once a capture is known to be rotated', () => {
    current = sample(scene({ up: '+z' }))
    render(<Control />)
    const label = screen.getByText('box').closest('label')
    expect(label?.querySelector('[role="checkbox"]')).toBeDisabled()
    expect(screen.getByText(/rotated/i)).toBeInTheDocument()
  })

  it('does not claim an unrecorded orientation means nothing is set', () => {
    current = sample(scene({ up: null }))
    render(<Control />)
    // The honest caption, not silence and not a false "nothing is set".
    expect(screen.getByText(/up not recorded/i)).toBeInTheDocument()
    expect(screen.queryByText(/nothing is set/i)).toBeNull()
  })
})
