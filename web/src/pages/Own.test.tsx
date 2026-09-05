import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SetupState, Status } from '../types'

// Captures every call `send` makes as [cmd, arg ?? id] - flat, because the test only
// ever cares about one call at a time, not a call log. `hosted: true` here matches every
// test in the main describe block below: they all pass a full SetupState, which only
// makes sense once a host exists to have pushed it. The hosted/browser split itself -
// `hosted` read from window.__TAURI__ at import time - gets its own describe block
// against the real bridge, the same way CompanionApp.test.tsx tests that split.
const sent: unknown[] = []
function mockBridge() {
  vi.doMock('../bridge', () => ({
    hosted: true,
    send: (cmd: string, id?: string, arg?: Record<string, unknown>) => {
      sent.length = 0
      sent.push(cmd, arg ?? id)
    },
  }))
}
mockBridge()

// Own talks to the plugin directly through useStatus/api.ts - a different channel from
// the bridge mocked above - so a test can say whether the plugin answered without doing
// a real network fetch inside jsdom.
let pluginState: Status | null = null
let pluginLive = false
vi.mock('../useStatus', () => ({
  useStatus: () => ({ state: pluginState, live: pluginLive, refresh: async () => pluginState }),
}))

const state = (over: Partial<SetupState>): SetupState =>
  ({
    game: '/games/velocidrone', mod: '0.1.0.0', bundledMod: '0.1.0.0', missing: [],
    ready: true, running: false, busy: null, busyPercent: null, launchArgs: '',
    lanUrl: 'http://192.168.1.42:8777/', tracks: [], catalog: null,
    unbound: [], trueLens: null, ...over,
  }) as SetupState

describe('create your own', () => {
  beforeEach(() => {
    pluginState = null
    pluginLive = false
  })

  // The two halves need opposite conditions: putting files down and writing the track
  // database need the game closed, and tuning needs it open. Whichever way round it is,
  // exactly one half is live and the other says why not.
  it('lets you add a capture while the game is closed', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({ running: false })} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeEnabled()
  })

  it('refuses to add a capture while the game is running, and says why', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeDisabled()
    expect(screen.getByText(/close velocidrone/i)).toBeInTheDocument()
  })

  it('sends the track name and the capture together', async () => {
    const { default: Own } = await import('./Own')
    const user = userEvent.setup()
    render(<Own state={state({
      running: false,
      unbound: [{ name: 'my-house', splats: 1, collision: false, bytes: 1 }],
    })} busy={false} />)
    const field = screen.getByLabelText(/track name/i) as HTMLInputElement
    // The default is derived from the capture, so the common path is one click.
    expect(field.value).toBe('VDGS my-house')
    await user.click(screen.getByRole('button', { name: /create track/i }))
    expect(sent).toEqual(['createTrack', { name: 'VDGS my-house', capture: 'my-house' }])
  })

  // removeCapture already existed end to end (Host::remove_capture, a dispatch arm,
  // game::remove_capture with its own path-traversal fix) with nothing in web/src ever
  // sending it - ① could only ever add a .ply, never take one back out.
  it('sends removeCapture with the selected capture\'s name', async () => {
    const { default: Own } = await import('./Own')
    const user = userEvent.setup()
    render(<Own state={state({
      running: false,
      unbound: [{ name: 'my-house', splats: 1, collision: false, bytes: 1 }],
    })} busy={false} />)
    await user.click(screen.getByRole('button', { name: /remove my-house/i }))
    expect(sent).toEqual(['removeCapture', 'my-house'])
  })

  it('disables removing a capture while the game runs', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({
      running: true,
      unbound: [{ name: 'my-house', splats: 1, collision: false, bytes: 1 }],
    })} busy={false} />)
    expect(screen.getByRole('button', { name: /remove my-house/i })).toBeDisabled()
  })

  // A state that has not landed yet is not the same as "nothing to explain" - the button
  // must still say why it is disabled, not go quiet.
  it('explains a disabled Add-a-.ply even before the host has pushed a first state', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={null} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeDisabled()
    expect(screen.getByText(/loading/i)).toBeInTheDocument()
  })

  // ① installs a .ply and the host pushes a fresh `unbound` list back down; the person
  // just picked that exact file from a dialog, so ②'s picker should already be on it
  // rather than making them find it again in a dropdown.
  it('selects a capture that newly appears in unbound', async () => {
    const { default: Own } = await import('./Own')
    const captureA = { name: 'my-house', splats: 1, collision: false, bytes: 1 }
    const captureB = { name: 'garage', splats: 2, collision: false, bytes: 2 }
    const { rerender } = render(
      <Own state={state({ running: false, unbound: [captureA] })} busy={false} />,
    )
    expect(screen.getByRole('button', { name: /remove my-house/i })).toBeInTheDocument()

    rerender(<Own state={state({ running: false, unbound: [captureA, captureB] })} busy={false} />)

    expect(screen.getByRole('button', { name: /remove garage/i })).toBeInTheDocument()
    const field = screen.getByLabelText(/track name/i) as HTMLInputElement
    expect(field.value).toBe('VDGS garage')
  })

  // The `edited` flag exists so a hand-typed name survives an unrelated click; a capture
  // arriving on its own is not the person's action either, and must be held to the same
  // rule - it may move the picker, but it may not touch a name they already wrote.
  it('does not overwrite a hand-typed track name when a capture arrives', async () => {
    const { default: Own } = await import('./Own')
    const user = userEvent.setup()
    const captureA = { name: 'my-house', splats: 1, collision: false, bytes: 1 }
    const captureB = { name: 'garage', splats: 2, collision: false, bytes: 2 }
    const { rerender } = render(
      <Own state={state({ running: false, unbound: [captureA] })} busy={false} />,
    )
    const field = screen.getByLabelText(/track name/i) as HTMLInputElement
    await user.clear(field)
    await user.type(field, 'My Own Name')
    expect(field.value).toBe('My Own Name')

    rerender(<Own state={state({ running: false, unbound: [captureA, captureB] })} busy={false} />)

    // The picker still moves to the new capture - only the name is protected.
    expect(screen.getByRole('button', { name: /remove garage/i })).toBeInTheDocument()
    expect((screen.getByLabelText(/track name/i) as HTMLInputElement).value).toBe('My Own Name')
  })

  // Once a capture arrival has moved the selection off `unbound[0]`, a later push that
  // does not add anything - a fresh array from the same JSON, nothing new in it - must
  // not silently pull the selection back to the first entry.
  it('leaves the selection alone when a push changes nothing about unbound', async () => {
    const { default: Own } = await import('./Own')
    const captureA = { name: 'my-house', splats: 1, collision: false, bytes: 1 }
    const captureB = { name: 'garage', splats: 2, collision: false, bytes: 2 }
    const { rerender } = render(
      <Own state={state({ running: false, unbound: [captureA] })} busy={false} />,
    )
    rerender(<Own state={state({ running: false, unbound: [captureA, captureB] })} busy={false} />)
    expect(screen.getByRole('button', { name: /remove garage/i })).toBeInTheDocument()

    // Same names, new array/object instances - exactly what a re-serialized push looks
    // like - and nothing else about the state changed either.
    rerender(<Own state={state({
      running: false,
      unbound: [{ ...captureA }, { ...captureB }],
    })} busy={false} />)

    expect(screen.getByRole('button', { name: /remove garage/i })).toBeInTheDocument()
  })

  // Every capture is "new" to a component that has never run before, so the very first
  // push must not be read as a batch of arrivals - that would fight the existing
  // unbound[0] default and, worse, would be ambiguous about which of several captures
  // "just" arrived when none of them did.
  it('does not treat the first push as an arrival - the initial list keeps its default', async () => {
    const { default: Own } = await import('./Own')
    const captureA = { name: 'my-house', splats: 1, collision: false, bytes: 1 }
    const captureB = { name: 'garage', splats: 2, collision: false, bytes: 2 }
    render(<Own state={state({ running: false, unbound: [captureA, captureB] })} busy={false} />)

    expect(screen.getByRole('button', { name: /remove my-house/i })).toBeInTheDocument()
    const field = screen.getByLabelText(/track name/i) as HTMLInputElement
    expect(field.value).toBe('VDGS my-house')
  })

  it('offers the LAN address once the game is running', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByText('http://192.168.1.42:8777/')).toBeInTheDocument()
  })

  // The rest of the app opts out of text selection at the shell; this address is the
  // one thing on this screen that exists only to be typed or copied onto a second
  // device. Class assertion only - jsdom cannot model an actual drag-select.
  it('keeps the LAN address selectable', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByText('http://192.168.1.42:8777/')).toHaveClass('select-text')
  })

  // The address exists unconditionally in real state (state.rs asks the OS for it
  // regardless of whether the game is running) - `lanUrl` alone is not proof anything is
  // listening there. A person scanning it while the game is closed gets connection
  // refused, and nothing on screen would have said why to expect that.
  it('does not offer the LAN address while the game is closed, even with one cached', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({ running: false })} busy={false} />)
    expect(screen.queryByText('http://192.168.1.42:8777/')).toBeNull()
  })

  it('does not offer the LAN address when there is none to show', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({ running: true, lanUrl: null })} busy={false} />)
    expect(screen.queryByText(/192\.168/)).toBeNull()
  })

  // Reachability is "did the plugin's own /api/status just answer", not "does the host
  // think the game process is running" - the two come from different channels, and only
  // the first is true in both builds. running:true with the plugin unreachable is exactly
  // the gap between them.
  it('shows the fly-first fallback while the plugin has not answered, host state aside', async () => {
    const { default: Own } = await import('./Own')
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByText(/fly first/i)).toBeInTheDocument()
  })
})

// Against the real bridge, not the stub above: `hosted` is read from window.__TAURI__ at
// module-eval time, so exercising the split means letting Own import the real thing and
// controlling window.__TAURI__ around it - the same way CompanionApp.test.tsx does for
// its own hosted/browser split.
describe('the hosted/browser split', () => {
  beforeEach(() => {
    vi.doUnmock('../bridge')
    vi.resetModules()
    pluginState = null
    pluginLive = false
  })

  afterEach(() => {
    delete (window as any).__TAURI__
    mockBridge()
    vi.resetModules()
  })

  it('renders ① and ② when hosted', async () => {
    ;(window as any).__TAURI__ = {
      core: { invoke: vi.fn() },
      event: { listen: vi.fn().mockResolvedValue(() => {}) },
    }
    const { default: Own } = await import('./Own')
    render(<Own state={state({
      running: false,
      unbound: [{ name: 'my-house', splats: 1, collision: false, bytes: 1 }],
    })} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeInTheDocument()
    expect(screen.getByLabelText(/track name/i)).toBeInTheDocument()
  })

  // This is the case the plugin actually serves: opened straight off the plugin's own
  // page, with no host at all. ① and ② could not work even in principle there - no file
  // picker, no database to write from a browser tab - so they must not render rather than
  // sitting there disabled forever. ③ is not merely "not broken" either: its own controls
  // have to actually appear, because that is the half this build serves.
  it('renders only ③, with its real controls, in a plain browser', async () => {
    delete (window as any).__TAURI__
    pluginLive = true
    pluginState = {
      track: null,
      loaded: ['my-house'],
      available: [{
        name: 'my-house', source: 'local', kind: 'ply', splats: 10, hasCollision: false,
        shown: true, scale: 1, y: 0, x: 0, z: 0, up: '+y', turn: 0, mirror: false,
        backdrop: false, collision: false, collisionView: 'off',
      }],
      bindings: {},
    }
    const { default: Own } = await import('./Own')
    render(<Own state={null} busy={false} />)
    expect(screen.queryByRole('button', { name: /add a \.ply/i })).toBeNull()
    expect(screen.queryByLabelText(/track name/i)).toBeNull()
    // The tuning controls themselves, not just the section around them - the shown
    // capture's own dial block (an <h2>), not merely its row in the capture list.
    expect(screen.getByRole('heading', { name: 'my-house' })).toBeInTheDocument()
    expect(screen.getByText(/^turn$/i)).toBeInTheDocument()
  })
})
