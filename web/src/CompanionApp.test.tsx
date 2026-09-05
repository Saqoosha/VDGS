import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { SetupState } from './types'

/**
 * The window merges four kinds of message from the host, and three of them are partial on
 * purpose: rebuilding the whole state for a progress tick would walk the disk a hundred
 * times during one download. That merging is the part with somewhere to go wrong.
 */
type Push =
  | ({ type: 'state' } & SetupState)
  | { type: 'log'; line: string }
  | { type: 'progress'; percent: number | null }
  | { type: 'busy'; what: string | null }
  | { type: 'running'; running: boolean }

let deliver: (m: Push) => void = () => {}

// hosted: true here, not read from window.__TAURI__ - these tests exercise the merge
// logic against a stubbed transport and expect the full shell (Setup tab, Fly button).
// The dedicated describe block below tests the hosted/browser split itself, against the
// real bridge, since that split is computed from window.__TAURI__ at import time.
function mockBridge() {
  vi.doMock('./bridge', () => ({
    hosted: true,
    send: vi.fn(),
    subscribe: (fn: (m: Push) => void) => {
      deliver = fn
      return () => {}
    },
  }))
}
mockBridge()

const base: SetupState = {
  game: 'C:\\game',
  mod: '0.1.0.0',
  bundledMod: '0.1.0.0',
  missing: [],
  ready: true,
  running: false,
  busy: null,
  busyPercent: null,
  launchArgs: '-force-d3d12',
  lanUrl: null,
  tracks: [],
  unbound: [],
  catalog: null,
  trueLens: false,
}

describe('the companion window', () => {
  beforeEach(async () => {
    const { default: CompanionApp } = await import('./CompanionApp')
    render(<CompanionApp />)
    deliver({ type: 'state', ...base })
  })

  it('shows how far along a download is', async () => {
    deliver({ type: 'busy', what: 'downloading FDF' })
    deliver({ type: 'progress', percent: 42 })
    // Twice on purpose: in the masthead, and beside the buttons that started it.
    await waitFor(() => expect(screen.getAllByText(/42%/)).toHaveLength(2))
  })

  it('says it is working before it knows how far along', async () => {
    deliver({ type: 'busy', what: 'installing the mod' })
    await waitFor(() => expect(screen.getByText(/working/i)).toBeInTheDocument())
  })

  it('goes back to ready when the work is done', async () => {
    deliver({ type: 'busy', what: 'installing the mod' })
    await waitFor(() => expect(screen.getByText(/working/i)).toBeInTheDocument())
    deliver({ type: 'state', ...base })
    await waitFor(() => expect(screen.getByText(/ready/i)).toBeInTheDocument())
  })

  // The two directions do not arrive the same way, and the test says so because getting
  // that backwards is how a passing test covers a message nobody sends: the game
  // starting is one flag, because rebuilding a whole state walks every capture on disk
  // for a bool - the game ending is a whole state, because it held the track database
  // open while it ran. Nobody presses refresh to say they quit, so both have to land or
  // Fly stays dead.
  it('follows the game starting and ending', async () => {
    const fly = () => screen.getByRole('button', { name: /^fly$/i })
    // Wait for the first state to land before asserting anything. Fly is disabled while
    // state is still null - no game path is known yet - so a "disabled" assertion made
    // straight away passes on an empty window and tests nothing at all. This test read
    // green with the merge deleted until that wait was added.
    await waitFor(() => expect(fly()).toBeEnabled())
    deliver({ type: 'running', running: true })
    await waitFor(() => expect(fly()).toBeDisabled())
    deliver({ type: 'state', ...base, running: false })
    await waitFor(() => expect(fly()).toBeEnabled())
  })

  it('keeps the log', async () => {
    deliver({ type: 'log', line: '12:00:00  installed FDF-2026-08-24' })
    await waitFor(() =>
      expect(screen.getByText(/installed FDF-2026-08-24/)).toBeInTheDocument(),
    )
  })

  // This only proves the class the browser reads to turn off selection is present on
  // the shell - jsdom does not implement drag-to-select, so it cannot prove a drag stops
  // painting a selection. `.h-svh` picks out the shell's own root div: `select-none`
  // alone would also match the Fly button and other shadcn controls that carry it too.
  it('turns off text selection at the window shell', () => {
    expect(document.querySelector('.h-svh')).toHaveClass('select-none')
  })
})

// Against the real bridge, not the stub above: `hosted` is read from window.__TAURI__ at
// module-eval time, so exercising the split means letting CompanionApp import the real
// thing and controlling window.__TAURI__ around it, the same way bridge.test.ts does.
describe('the three-tab shell', () => {
  beforeEach(() => {
    vi.doUnmock('./bridge')
    vi.resetModules()
  })

  afterEach(() => {
    delete (window as any).__TAURI__
    mockBridge()
    vi.resetModules()
  })

  it('shows three tabs when hosted', async () => {
    ;(window as any).__TAURI__ = {
      core: { invoke: vi.fn() },
      event: { listen: vi.fn().mockResolvedValue(() => {}) },
    }
    const { default: CompanionApp } = await import('./CompanionApp')
    render(<CompanionApp />)
    expect(screen.getByRole('tab', { name: /setup/i })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /tracks/i })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /create your own/i })).toBeInTheDocument()
  })

  // Opened in a browser there is no host to pick a folder or download anything, so the
  // two tabs that do only that would be a wall of dead buttons.
  it('shows only create-your-own in a plain browser', async () => {
    const { default: CompanionApp } = await import('./CompanionApp')
    render(<CompanionApp />)
    expect(screen.queryByRole('tab', { name: /setup/i })).toBeNull()
    expect(screen.queryByRole('tab', { name: /tracks/i })).toBeNull()
    expect(screen.getByRole('tab', { name: /create your own/i })).toBeInTheDocument()
  })
})
