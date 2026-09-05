import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'
import Own from './Own'
import type { SetupState } from '../types'

// Captures every call `send` makes as [cmd, arg ?? id] - flat, because the test only
// ever cares about one call at a time, not a call log.
const sent: unknown[] = []
vi.mock('../bridge', () => ({
  send: (cmd: string, id?: string, arg?: Record<string, unknown>) => {
    sent.length = 0
    sent.push(cmd, arg ?? id)
  },
}))

const state = (over: Partial<SetupState>): SetupState =>
  ({
    game: '/games/velocidrone', mod: '0.1.0.0', bundledMod: '0.1.0.0', missing: [],
    ready: true, running: false, busy: null, busyPercent: null, launchArgs: '',
    lanUrl: 'http://192.168.1.42:8777/', tracks: [], catalog: null,
    unbound: [], trueLens: null, ...over,
  }) as SetupState

describe('create your own', () => {
  // The two halves need opposite conditions: putting files down and writing the track
  // database need the game closed, and tuning needs it open. Whichever way round it is,
  // exactly one half is live and the other says why not.
  it('lets you add a capture while the game is closed', () => {
    render(<Own state={state({ running: false })} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeEnabled()
  })

  it('refuses to add a capture while the game is running, and says why', () => {
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByRole('button', { name: /add a \.ply/i })).toBeDisabled()
    expect(screen.getByText(/close velocidrone/i)).toBeInTheDocument()
  })

  it('sends the track name and the capture together', async () => {
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

  it('offers the LAN address only where the tuning controls are', () => {
    render(<Own state={state({ running: true })} busy={false} />)
    expect(screen.getByText('http://192.168.1.42:8777/')).toBeInTheDocument()
  })
})
