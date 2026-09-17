import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
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

function mockFailingFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response('{"error":"nope"}', { status: 400 })),
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
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
    lodDetail: 10,
    lodBudget: 3_000_000,
    lod: null,
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

// Each is a control that would otherwise look broken with no explanation.
describe('controls that explain themselves', () => {
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

// Mirror is the one control that reparses the whole .ply (a despawn+respawn, not a
// transform update), so it is the one control with a pending state. These tests exercise
// the state machine itself - a jsdom test cannot model a status endpoint that genuinely
// stalls for several real seconds, so "the poll landed" is simulated by re-rendering with
// a `state` prop carrying the new value, the same way the real app's poll loop would push
// a fresh prop down after `/api/status` finally answers.
describe('mirror pending state', () => {
  function mirrorCheckbox() {
    return screen.getByText('mirror').closest('label')?.querySelector('[role="checkbox"]')
  }

  it('shows a pending state immediately and disables the control', async () => {
    mockOkFetch()
    const status = sample(scene({ kind: 'ply', mirror: false }))
    const refresh = vi.fn().mockResolvedValue(status)
    render(<Control state={status} refresh={refresh} />)

    fireEvent.click(mirrorCheckbox()!)

    expect(screen.getByText(/respawning/i)).toBeInTheDocument()
    expect(mirrorCheckbox()).toBeDisabled()
  })

  it('clears once the polled status reports the requested value', async () => {
    mockOkFetch()
    const before = sample(scene({ kind: 'ply', mirror: false }))
    const after = sample(scene({ kind: 'ply', mirror: true }))
    const refresh = vi.fn().mockResolvedValue(after)
    const { rerender } = render(<Control state={before} refresh={refresh} />)

    fireEvent.click(mirrorCheckbox()!)
    expect(screen.getByText(/respawning/i)).toBeInTheDocument()

    // The ambient poll landing: a fresh `state` prop, not a resolved promise - the
    // component watches `scene.mirror` on the prop it was handed, not the return value
    // of the refresh it happened to trigger itself.
    rerender(<Control state={after} refresh={refresh} />)

    await waitFor(() => expect(screen.queryByText(/respawning/i)).toBeNull())
    expect(mirrorCheckbox()).not.toBeDisabled()
  })

  it('does not clear on a poll that still reports the old value', async () => {
    mockOkFetch()
    const before = sample(scene({ kind: 'ply', mirror: false }))
    const refresh = vi.fn().mockResolvedValue(before)
    const { rerender } = render(<Control state={before} refresh={refresh} />)

    fireEvent.click(mirrorCheckbox()!)
    // Same object reference is fine here (React still re-renders on any parent update);
    // the point is `mirror` itself has not changed.
    rerender(<Control state={{ ...before }} refresh={refresh} />)

    expect(screen.getByText(/respawning/i)).toBeInTheDocument()
    expect(mirrorCheckbox()).toBeDisabled()
  })

  it('gives up honestly after the bound if nothing ever confirms the change', async () => {
    vi.useFakeTimers()
    mockOkFetch()
    const status = sample(scene({ kind: 'ply', mirror: false }))
    const refresh = vi.fn().mockResolvedValue(status)
    render(<Control state={status} refresh={refresh} />)

    fireEvent.click(mirrorCheckbox()!)
    expect(screen.getByText(/respawning/i)).toBeInTheDocument()

    // Flush the mocked fetch's microtask, then run out the clock on the bound. Both need
    // `act` around them - the state updates they trigger (onRefresh resolving, then the
    // timeout firing) happen outside the synchronous event React's own `act` wraps
    // `fireEvent` in.
    await act(() => vi.advanceTimersByTimeAsync(0))
    await act(() => vi.advanceTimersByTimeAsync(20_000))

    expect(screen.queryByText(/respawning/i)).toBeNull()
    expect(mirrorCheckbox()).not.toBeDisabled()
    expect(screen.getByText(/no confirmation/i)).toBeInTheDocument()
  })

  it('clears right away when the request itself fails, rather than waiting out the bound', async () => {
    mockFailingFetch()
    const status = sample(scene({ kind: 'ply', mirror: false }))
    const refresh = vi.fn().mockResolvedValue(status)
    render(<Control state={status} refresh={refresh} />)

    fireEvent.click(mirrorCheckbox()!)
    expect(screen.getByText(/respawning/i)).toBeInTheDocument()

    await waitFor(() => expect(screen.queryByText(/respawning/i)).toBeNull())
    expect(mirrorCheckbox()).not.toBeDisabled()
  })
})

// The number field beside a dial takes a whole value, not a keystroke at a time: sending
// on every change applied "4" on the way to "44", and reformatting the controlled value
// each render overwrote what was being typed.
describe('typing a number into a dial', () => {
  function scaleField() {
    return screen.getAllByRole('spinbutton')[1] as HTMLInputElement
  }

  it('sends once, on Enter, with the whole number', async () => {
    mockOkFetch()
    render(<Control state={sample(scene({ scale: 1 }))} refresh={vi.fn().mockResolvedValue(null)} />)
    const field = scaleField()
    fireEvent.focus(field)
    fireEvent.change(field, { target: { value: '4' } })
    fireEvent.change(field, { target: { value: '44' } })
    expect(field.value).toBe('44')
    expect(fetch).not.toHaveBeenCalled()
    fireEvent.keyDown(field, { key: 'Enter' })
    fireEvent.blur(field)
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
    const body = JSON.parse((vi.mocked(fetch).mock.calls[0][1] as RequestInit).body as string)
    expect(body.scale).toBe(44)
  })

  it('drops the edit on Escape', () => {
    mockOkFetch()
    render(<Control state={sample(scene({ scale: 1 }))} refresh={vi.fn().mockResolvedValue(null)} />)
    const field = scaleField()
    fireEvent.focus(field)
    fireEvent.change(field, { target: { value: '9' } })
    fireEvent.keyDown(field, { key: 'Escape' })
    expect(field.value).toBe('1.000')
    expect(fetch).not.toHaveBeenCalled()
  })
})
