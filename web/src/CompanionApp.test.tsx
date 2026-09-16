import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
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
  | { type: 'picked'; path: string; stem: string }

let deliver: (m: Push) => void = () => {}

// hosted: true here, not read from window.__TAURI__ - these tests exercise the merge
// logic against a stubbed transport and expect the full shell (setup strip, Fly button).
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
    // Once: on the bar at the head of the table (no row claims "FDF" here). The
    // masthead names the job and leaves the number to the bar or the row's ring.
    await waitFor(() => expect(screen.getAllByText(/42%/)).toHaveLength(1))
    expect(screen.getByRole('progressbar', { name: /downloading FDF/i })).toHaveAttribute(
      'aria-valuenow',
      '42',
    )
  })

  it('names the job in the masthead while it runs', async () => {
    deliver({ type: 'busy', what: 'installing the mod' })
    await waitFor(() => expect(screen.getAllByText(/installing the mod/i).length).toBeGreaterThan(0))
  })

  it('goes back to ready when the work is done', async () => {
    deliver({ type: 'busy', what: 'installing the mod' })
    await waitFor(() => expect(screen.getAllByText(/installing the mod/i).length).toBeGreaterThan(0))
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

  // The log used to live on one tab while the buttons that write to it lived on the
  // others, so a failure landed where nobody was looking. The newest line sits in the
  // masthead on every screen; the whole log opens from it.
  it('shows the newest log line in the masthead, and the whole log on request', async () => {
    deliver({ type: 'log', line: '12:00:00  installed FDF-2026-08-24' })
    deliver({ type: 'log', line: '12:00:01  failed: no such file' })
    await waitFor(() => expect(screen.getByText(/failed: no such file/)).toBeInTheDocument())
    expect(screen.queryByText(/installed FDF-2026-08-24/)).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: /show the log/i }))
    expect(screen.getByText(/installed FDF-2026-08-24/)).toBeInTheDocument()
    // select-text: the log is what a person copies into a bug report.
    expect(screen.getByTestId('log')).toHaveClass('select-text')
  })

  // The host answers pickPly with the path; the name row is the page's part.
  it('opens the name row when the host reports a picked file', async () => {
    deliver({ type: 'picked', path: '/d/himeji-lod2.ply', stem: 'himeji-lod2' })
    await waitFor(() =>
      expect(screen.getByRole('textbox', { name: /track name/i })).toHaveValue('VDGS himeji-lod2'),
    )
  })

  // This only proves the class the browser reads to turn off selection is present on
  // the shell - jsdom does not implement drag-to-select, so it cannot prove a drag stops
  // painting a selection. A data-testid, not a styling class, picks out the shell's own
  // root div: `select-none` alone would also match the Fly button and other shadcn
  // controls that carry it too, and the sizing class that used to double as that handle
  // (`.h-svh`) moved off this element once the page started scrolling as a whole instead
  // of being clipped to one screen.
  it('turns off text selection at the window shell', () => {
    expect(screen.getByTestId('companion-shell')).toHaveClass('select-none')
  })
})

// Against the real bridge, not the stub above: `hosted` is read from window.__TAURI__ at
// module-eval time, so exercising the split means letting CompanionApp import the real
// thing and controlling window.__TAURI__ around it, the same way bridge.test.ts does.
describe('the one-page shell', () => {
  beforeEach(() => {
    vi.doUnmock('./bridge')
    vi.resetModules()
  })

  afterEach(() => {
    delete (window as any).__TAURI__
    mockBridge()
    vi.resetModules()
  })

  it('shows the setup strip, the track table and Fly when hosted, with no tabs', async () => {
    ;(window as any).__TAURI__ = {
      core: { invoke: vi.fn() },
      event: { listen: vi.fn().mockResolvedValue(() => {}) },
    }
    const { default: CompanionApp } = await import('./CompanionApp')
    render(<CompanionApp />)
    expect(screen.queryByRole('tab')).toBeNull()
    expect(screen.getByRole('button', { name: /change…/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /add track/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^fly$/i })).toBeInTheDocument()
  })

  // Opened in a browser there is no host to pick a folder, download anything or write
  // the track database, so the strip, the table and Fly would be a wall of dead
  // buttons. What is left is the half the plugin serves: tuning.
  it('shows only the tuning screen in a plain browser', async () => {
    const { default: CompanionApp } = await import('./CompanionApp')
    render(<CompanionApp />)
    expect(screen.queryByRole('button', { name: /add track/i })).toBeNull()
    expect(screen.queryByRole('button', { name: /^fly$/i })).toBeNull()
    expect(screen.getByText(/waiting for the plugin/i)).toBeInTheDocument()
  })
})
