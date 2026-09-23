import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { SetupStrip } from './SetupStrip'
import { send } from './bridge'
import { rememberLang } from './i18n'
import type { SetupState } from './types'

vi.mock('./bridge', () => ({ send: vi.fn() }))

function state(over: Partial<SetupState> = {}): SetupState {
  return {
    game: 'C:\\game',
    mod: '0.1.0.0',
    bundledMod: '0.1.0.0',
    missing: [],
    ready: true,
    running: false,
    busy: null,
    busyPercent: null,
    catalog: null,
    launchArgs: '-force-d3d12',
    lanUrl: null,
    tracks: [],
    unbound: [],
    trueLens: false,
    ...over,
  }
}

describe('the setup strip', () => {
  it('says what is missing rather than only that something is', () => {
    render(<SetupStrip state={state({ missing: ['BepInEx', 'the shader bundle'], ready: false })} />)
    expect(screen.getByText(/missing: BepInEx · the shader bundle/i)).toBeInTheDocument()
  })

  // Right after Uninstall the mod and the bundle are both gone, and that is the normal
  // state, not a fault: the red list is for an install with pieces missing.
  it('calls a machine with no mod "not installed", not broken', () => {
    render(
      <SetupStrip
        state={state({ mod: null, missing: ['the mod', 'the shader bundle'], ready: false })}
      />,
    )
    expect(screen.getByText(/not installed/i)).toBeInTheDocument()
    expect(screen.queryByText(/missing:/i)).toBeNull()
  })

  it('will not offer to install or reinstall the mod while the game is running', () => {
    render(<SetupStrip state={state({ running: true })} />)
    expect(screen.getByRole('button', { name: /reinstall mod/i })).toBeDisabled()
  })

  it('says which of install, reinstall or update the button will do', () => {
    // Installing over a working setup to find out is the failure this avoids.
    const { rerender } = render(<SetupStrip state={state({ mod: null })} />)
    expect(screen.getByRole('button', { name: /^install mod$/i })).toBeInTheDocument()

    rerender(<SetupStrip state={state()} />)
    expect(screen.getByRole('button', { name: /^reinstall mod$/i })).toBeInTheDocument()

    rerender(<SetupStrip state={state({ bundledMod: '0.2.0.0' })} />)
    expect(screen.getByRole('button', { name: /update to 0\.2\.0\.0/i })).toBeInTheDocument()
  })

  it('shows its own jobs under its buttons, and starts nothing else meanwhile', () => {
    // Installing copies forty-odd files past a virus scanner. Without this the window
    // looks like the click did nothing.
    const { rerender } = render(<SetupStrip state={state({ busy: 'installing the mod' })} />)
    expect(screen.getByRole('progressbar', { name: /installing the mod/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /reinstall mod/i })).toBeDisabled()
    expect(screen.getByRole('button', { name: /uninstall/i })).toBeDisabled()
    // A download is the table's business, not this section's.
    rerender(<SetupStrip state={state({ busy: 'downloading FDF' })} />)
    expect(screen.queryByRole('progressbar')).toBeNull()
  })

  it('will not offer to uninstall a mod that is not there', () => {
    render(<SetupStrip state={state({ mod: null })} />)
    expect(screen.getByRole('button', { name: /uninstall/i })).toBeDisabled()
  })

  it('does not offer to install a mod it is not carrying', () => {
    render(<SetupStrip state={state({ bundledMod: null })} />)
    expect(screen.getByRole('button', { name: /no mod payload/i })).toBeDisabled()
  })

  // The game path is what a person copies into a bug report - collect-mac-diagnostics.sh
  // exists because getting that out of people matters. Class assertion only: jsdom does
  // not model an actual drag-select.
  it('keeps the game path selectable, unlike the rest of the shell', () => {
    render(<SetupStrip state={state()} />)
    expect(screen.getByText('C:\\game')).toHaveClass('select-text')
  })

  it('warns about True Lens only when the game has it on', () => {
    // null = unknown and false = off must not warn; only true is shown.
    rememberLang('en')
    const { rerender } = render(<SetupStrip state={state({ trueLens: null })} />)
    expect(screen.queryByText(/True Lens/)).toBeNull()
    rerender(<SetupStrip state={state({ trueLens: false })} />)
    expect(screen.queryByText(/True Lens/)).toBeNull()
    rerender(<SetupStrip state={state({ trueLens: true })} />)
    expect(screen.getByText('True Lens').closest('[lang]')?.getAttribute('lang')).toBe('en')
    // The symptom leads: it is what makes someone read the rest, so it is asserted
    // rather than left to whatever wording the sentence happens to carry.
    expect(screen.getByText(/scans will not appear/i)).toBeInTheDocument()
    expect(screen.getByText(/never reach the screen/i)).toBeInTheDocument()
  })

  it('offers the newer app only when the host says there is one, and asks the host to open it', () => {
    rememberLang('en')
    const { rerender } = render(<SetupStrip state={state({ appUpdate: null })} />)
    expect(screen.queryByText(/newer version of this app/i)).toBeNull()
    rerender(<SetupStrip state={state({ appUpdate: '2026.09.24' })} />)
    expect(screen.getByText(/newer version of this app is out — 2026\.09\.24/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /download/i }))
    // No URL from the page: the host opens the catalog's own site and nothing else.
    expect(vi.mocked(send)).toHaveBeenCalledWith('openAppUpdate')
  })
})
