import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import Setup from './Setup'
import { rememberLang } from '../i18n'
import type { SetupState } from '../types'

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

describe('Setup', () => {
  it('says what is missing rather than only that something is', () => {
    render(<Setup state={state({ missing: ['BepInEx', 'the mod'], ready: false })} log={[]} />)
    expect(screen.getByText(/missing: BepInEx · the mod/i)).toBeInTheDocument()
  })

  it('will not offer to install or reinstall the mod while the game is running', () => {
    render(<Setup state={state({ running: true })} log={[]} />)
    expect(screen.getByRole('button', { name: /reinstall mod/i })).toBeDisabled()
  })

  it('says which of install, reinstall or update the button will do', () => {
    // Installing over a working setup to find out is the failure this avoids.
    const { rerender } = render(<Setup state={state({ mod: null })} log={[]} />)
    expect(screen.getByRole('button', { name: /^install mod$/i })).toBeInTheDocument()

    rerender(<Setup state={state()} log={[]} />)
    expect(screen.getByRole('button', { name: /^reinstall mod$/i })).toBeInTheDocument()

    rerender(<Setup state={state({ bundledMod: '0.2.0.0' })} log={[]} />)
    expect(screen.getByRole('button', { name: /update to 0\.2\.0\.0/i })).toBeInTheDocument()
  })

  it('says what it is doing, and starts nothing else meanwhile', () => {
    // Installing copies forty-odd files past a virus scanner. Without this the window
    // looks like the click did nothing.
    render(<Setup state={state({ busy: 'installing the mod' })} log={[]} />)
    expect(screen.getByText(/installing the mod/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /reinstall mod/i })).toBeDisabled()
  })

  it('will not offer to uninstall a mod that is not there', () => {
    render(<Setup state={state({ mod: null })} log={[]} />)
    expect(screen.getByRole('button', { name: /uninstall/i })).toBeDisabled()
  })

  it('does not offer to install a mod it is not carrying', () => {
    render(<Setup state={state({ bundledMod: null })} log={[]} />)
    expect(screen.getByRole('button', { name: /no mod payload/i })).toBeDisabled()
  })

  it('holds the log until there is something in it', () => {
    const { rerender } = render(<Setup state={state()} log={[]} />)
    expect(screen.queryByText(/12:00:00/)).toBeNull()
    rerender(<Setup state={state()} log={['12:00:00  installed']} />)
    // getByText normalises whitespace, and the host pads the timestamp with two spaces.
    expect(screen.getByText(/12:00:00\s+installed/)).toBeInTheDocument()
  })

  // The log and the game path are what a person copies into a bug report -
  // collect-mac-diagnostics.sh exists because getting that out of people matters. Class
  // assertions only: jsdom does not model an actual drag-select.
  it('keeps the log and the game path selectable, unlike the rest of the shell', () => {
    render(<Setup state={state()} log={['12:00:00  installed']} />)
    expect(screen.getByText(/12:00:00\s+installed/).closest('ol')).toHaveClass('select-text')
    expect(screen.getByText('C:\\game')).toHaveClass('select-text')
  })

  it('warns about True Lens only when the game has it on', () => {
    // null = unknown and false = off must not warn; only true is shown.
    rememberLang('en')
    const { rerender } = render(<Setup state={state({ trueLens: null })} log={[]} />)
    expect(screen.queryByText(/True Lens/)).toBeNull()
    rerender(<Setup state={state({ trueLens: false })} log={[]} />)
    expect(screen.queryByText(/True Lens/)).toBeNull()
    rerender(<Setup state={state({ trueLens: true })} log={[]} />)
    expect(screen.getByText('True Lens').closest('[lang]')?.getAttribute('lang')).toBe('en')
    // The symptom leads: it is what makes someone read the rest, so it is asserted
    // rather than left to whatever wording the sentence happens to carry.
    expect(screen.getByText(/scans will not appear/i)).toBeInTheDocument()
    expect(screen.getByText(/never reach the screen/i)).toBeInTheDocument()
  })
})
