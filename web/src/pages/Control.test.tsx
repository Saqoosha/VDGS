import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Control from './Control'
import type { Scene, Status } from '../types'

// The backdrop checkbox's onChange actually POSTs (api.ts, unmocked) - stub fetch itself,
// the same way api.test.ts does, rather than mocking the api module and losing coverage
// of the real request path.
function mockOkFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response('{"ok":true}', { status: 200 })),
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
})

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

describe('Control XSS', () => {
  it('renders a hostile capture name as text, not HTML', () => {
    const status = sample(scene({ name: xss }))
    render(<Control state={status} refresh={async () => status} />)
    expect(screen.queryByRole('img')).toBeNull()
    expect(screen.getAllByText(xss).length).toBeGreaterThan(0)
  })
})

// The three things earlier reviews parked for this task - each is a control that would
// otherwise look broken with no explanation.
describe('the parked items', () => {
  it('hides Mirror for a converted capture - the flip happens while a .ply is parsed', () => {
    const status = sample(scene({ kind: 'converted' }))
    render(<Control state={status} refresh={async () => status} />)
    expect(screen.queryByText(/mirror/i)).toBeNull()
  })

  it('shows Mirror for a .ply capture', () => {
    const status = sample(scene({ kind: 'ply' }))
    render(<Control state={status} refresh={async () => status} />)
    expect(screen.getByText(/mirror/i)).toBeInTheDocument()
  })

  it('disables the backdrop box and says why once a capture is known to be rotated', () => {
    const status = sample(scene({ up: '+z' }))
    render(<Control state={status} refresh={async () => status} />)
    const label = screen.getByText('box').closest('label')
    expect(label?.querySelector('[role="checkbox"]')).toBeDisabled()
    expect(screen.getByText(/rotated/i)).toBeInTheDocument()
  })

  it('does not claim an unrecorded orientation means nothing is set', () => {
    const status = sample(scene({ up: null }))
    render(<Control state={status} refresh={async () => status} />)
    // The honest caption, not silence and not a false "nothing is set".
    expect(screen.getByText(/up not recorded/i)).toBeInTheDocument()
    expect(screen.queryByText(/nothing is set/i)).toBeNull()
  })

  // The backdrop box is left enabled on `up: null` on purpose - it can mean "never
  // touched" just as easily as "a legacy rotation" (the case above), and the plugin's own
  // IsUpright falls back to the raw `rotation` array in exactly that case, refusing to
  // attach while still answering 200. A checkbox that silently reverts itself on the next
  // poll is not an explanation - this is the explanation that has to replace it.
  it('says the backdrop was refused when a legacy rotation quietly declines it', async () => {
    mockOkFetch()
    const before = sample(scene({ up: null, backdrop: false }))
    // The scene the plugin actually reports back after the POST: backdrop still off,
    // because IsUpright read the raw rotation array and refused to attach.
    const after = sample(scene({ up: null, backdrop: false }))
    const refresh = vi.fn().mockResolvedValue(after)
    render(<Control state={before} refresh={refresh} />)

    const checkbox = screen.getByText('box').closest('label')?.querySelector('[role="checkbox"]')
    fireEvent.click(checkbox!)

    await screen.findByText(/backdrop refused/i)
    expect(screen.getByText(/this capture carries a rotation/i)).toBeInTheDocument()
  })

  it('says nothing when the backdrop request actually succeeds', async () => {
    mockOkFetch()
    const before = sample(scene({ up: '+y', backdrop: false }))
    const after = sample(scene({ up: '+y', backdrop: true }))
    const refresh = vi.fn().mockResolvedValue(after)
    render(<Control state={before} refresh={refresh} />)

    const checkbox = screen.getByText('box').closest('label')?.querySelector('[role="checkbox"]')
    fireEvent.click(checkbox!)

    await vi.waitFor(() => expect(refresh).toHaveBeenCalled())
    expect(screen.queryByText(/backdrop refused/i)).toBeNull()
  })
})
