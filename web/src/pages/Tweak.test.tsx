import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import Tweak from './Tweak'
import type { Scene, Status } from '../types'

let pluginState: Status | null = null
let pluginLive = false
vi.mock('../useStatus', () => ({
  useStatus: () => ({ state: pluginState, live: pluginLive, refresh: async () => pluginState }),
}))

function scene(over: Partial<Scene> = {}): Scene {
  return {
    name: 'himeji',
    source: 'local',
    kind: 'ply',
    splats: 1,
    hasCollision: false,
    shown: true,
    scale: 1,
    y: 0,
    x: 0,
    z: 0,
    up: '+y',
    turn: 0,
    mirror: true,
    backdrop: false,
    blackout: false,
    collision: true,
    collisionView: 'off',
    lodDetail: 10,
    lodBudget: 3_000_000,
    lod: null,
    ...over,
  } as Scene
}

function status(over: Partial<Status> = {}): Status {
  return { track: 'VDGS Himeji', loaded: ['himeji'], available: [scene()], bindings: {}, ...over }
}

describe('the tweak screen', () => {
  beforeEach(() => {
    pluginState = null
    pluginLive = false
  })

  it('shows the controls for the track it was opened on', () => {
    pluginLive = true
    pluginState = status()
    render(<Tweak track="VDGS Himeji" onBack={() => {}} />)
    expect(screen.getByText(/scale/i)).toBeInTheDocument()
  })

  // The controls follow the plugin's loaded capture. If the game moved to another track
  // while this was open, tuning here would write the wrong capture's placement.
  it('sends the person back to the table when the game changed track', () => {
    pluginLive = true
    pluginState = status({
      track: 'VDGS FDF',
      loaded: ['fdf'],
      available: [scene({ name: 'fdf' })],
    })
    render(<Tweak track="VDGS Himeji" onBack={() => {}} />)
    expect(screen.getByText(/the game moved to/i)).toBeInTheDocument()
    expect(screen.queryByText(/^scale$/i)).toBeNull()
  })

  // An older plugin answers without turn/x/z; the dials would crash on them.
  it('names an older mod instead of crashing on its status', () => {
    pluginLive = true
    const old = scene() as unknown as Record<string, unknown>
    delete old.turn
    delete old.x
    delete old.z
    pluginState = status({ available: [old as unknown as Scene] })
    render(<Tweak track="VDGS Himeji" onBack={() => {}} />)
    expect(screen.getByText(/older than this app/i)).toBeInTheDocument()
  })

  it('says the plugin is not answering when it is not', () => {
    render(<Tweak track="VDGS Himeji" onBack={() => {}} />)
    expect(screen.getByText(/not answering/i)).toBeInTheDocument()
  })

  it('shows LOD dials only when the shown scene reports lod stats', () => {
    pluginLive = true
    pluginState = status({
      available: [
        scene({
          name: 'ssog',
          kind: 'ssog',
          lod: { leaves: 10, activePerLevel: [1_200_000, 800_000] },
        }),
      ],
      loaded: ['ssog'],
    })
    render(<Tweak track="VDGS Himeji" onBack={() => {}} />)
    expect(screen.getByText(/LOD detail/i)).toBeInTheDocument()
    expect(screen.getByText(/LOD budget/i)).toBeInTheDocument()
    expect(screen.getByText(/L0 1.2M/)).toBeInTheDocument()
  })

  it('hides LOD dials when the scene has no lod stats', () => {
    pluginLive = true
    pluginState = status()
    render(<Tweak track="VDGS Himeji" onBack={() => {}} />)
    expect(screen.queryByText(/LOD detail/i)).toBeNull()
    expect(screen.queryByText(/LOD budget/i)).toBeNull()
  })
})
