import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import Setup from './Setup'
import type { SetupState } from '../types'

const xss = '<img src=x onerror=alert(1)>'

function state(over: Partial<SetupState> = {}): SetupState {
  return {
    game: 'C:\\game',
    mod: '0.1.0.0',
    missing: [],
    ready: true,
    running: false,
    launchArgs: '-force-d3d12',
    captures: [],
    ...over,
  }
}

describe('Setup', () => {
  it('says what is missing rather than only that something is', () => {
    render(<Setup state={state({ missing: ['BepInEx', 'the mod'], ready: false })} log={[]} />)
    expect(screen.getByText(/missing: BepInEx · the mod/i)).toBeInTheDocument()
  })

  it('names the launch flag, which the captures need in order to draw', () => {
    render(<Setup state={state()} log={[]} />)
    expect(screen.getByText(/-force-d3d12/)).toBeInTheDocument()
  })

  it('marks a capture with no collision mesh', () => {
    render(
      <Setup
        state={state({ captures: [{ name: 'testcube', splats: 640, collision: false }] })}
        log={[]}
      />,
    )
    expect(screen.getByText(/no collision/)).toBeInTheDocument()
  })

  it('renders a capture name as text', () => {
    // Capture folders are named by whoever made them, and an installed archive names its
    // own destination - so the name reaching this page is not ours.
    render(
      <Setup
        state={state({ captures: [{ name: xss, splats: 1, collision: true }] })}
        log={[]}
      />,
    )
    expect(screen.getByText(xss)).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })

  it('will not offer to install anything while the game is running', () => {
    render(<Setup state={state({ running: true })} log={[]} />)
    expect(screen.getByRole('button', { name: /install mod/i })).toBeDisabled()
    expect(screen.getByRole('button', { name: /^fly$/i })).toBeDisabled()
  })

  it('holds the log until there is something in it', () => {
    const { rerender } = render(<Setup state={state()} log={[]} />)
    expect(screen.queryByText(/^log$/i)).toBeNull()
    rerender(<Setup state={state()} log={['12:00:00  installed']} />)
    // getByText normalises whitespace, and the host pads the timestamp with two spaces.
    expect(screen.getByText(/12:00:00\s+installed/)).toBeInTheDocument()
  })
})
