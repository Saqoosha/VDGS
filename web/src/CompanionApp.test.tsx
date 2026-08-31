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

vi.mock('./bridge', () => ({
  send: vi.fn(),
  subscribe: (fn: (m: Push) => void) => {
    deliver = fn
    return () => {}
  },
}))

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
  tracks: [],
  unbound: [],
  catalog: null,
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

  // Nothing tells the app the game exited except the host noticing, so a whole-state
  // rebuild is not what arrives - and a merge that dropped this would leave Fly dead
  // until someone pressed refresh.
  it('lets you fly again once the game is closed', async () => {
    deliver({ type: 'state', ...base, running: true })
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /^fly$/i })).toBeDisabled(),
    )
    deliver({ type: 'running', running: false })
    await waitFor(() => expect(screen.getByRole('button', { name: /^fly$/i })).toBeEnabled())
  })

  it('keeps the log', async () => {
    deliver({ type: 'log', line: '12:00:00  installed FDF-2026-08-24' })
    await waitFor(() =>
      expect(screen.getByText(/installed FDF-2026-08-24/)).toBeInTheDocument(),
    )
  })
})
