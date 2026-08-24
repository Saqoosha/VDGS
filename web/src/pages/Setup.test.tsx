import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import Setup from './Setup'
import type { SetupState, TrackEntry } from '../types'

const xss = '<img src=x onerror=alert(1)>'

function track(over: Partial<TrackEntry> = {}): TrackEntry {
  return {
    track: 'VDGS FDF',
    capture: 'FDF-2026-08-24',
    splats: 1497617,
    bytes: 128_800_000,
    collision: true,
    captureInstalled: true,
    converted: true,
    inGame: true,
    ...over,
  }
}

function state(over: Partial<SetupState> = {}): SetupState {
  return {
    game: 'C:\\game',
    mod: '0.1.0.0',
    missing: [],
    ready: true,
    running: false,
    launchArgs: '-force-d3d12',
    tracks: [],
    unbound: [],
    ...over,
  }
}

describe('Setup', () => {
  it('says what is missing rather than only that something is', () => {
    render(<Setup state={state({ missing: ['BepInEx', 'the mod'], ready: false })} log={[]} />)
    expect(screen.getByText(/missing: BepInEx · the mod/i)).toBeInTheDocument()
  })

  it('lists a track with the capture it shows', () => {
    render(<Setup state={state({ tracks: [track()] })} log={[]} />)
    expect(screen.getByText('VDGS FDF')).toBeInTheDocument()
    expect(screen.getByText(/FDF-2026-08-24/)).toBeInTheDocument()
    expect(screen.getByText(/1,497,617 splats/)).toBeInTheDocument()
  })

  it('marks a capture with no collision mesh', () => {
    render(<Setup state={state({ tracks: [track({ collision: false })] })} log={[]} />)
    expect(screen.getByText(/no collision/)).toBeInTheDocument()
  })

  it('says when a bound capture is not on the machine', () => {
    // Silent otherwise: the track loads and simply shows nothing.
    render(
      <Setup
        state={state({ tracks: [track({ captureInstalled: false, capture: 'nelson-lod2' })] })}
        log={[]}
      />,
    )
    expect(screen.getByText(/nelson-lod2 is not installed/)).toBeInTheDocument()
  })

  it('says when a track a binding names is not in the game', () => {
    render(<Setup state={state({ tracks: [track({ inGame: false })] })} log={[]} />)
    expect(screen.getByText(/not in velocidrone/i)).toBeInTheDocument()
  })

  it('reports captures no track points at', () => {
    render(
      <Setup
        state={state({ unbound: [{ name: 'testcube', splats: 640, collision: false }] })}
        log={[]}
      />,
    )
    expect(screen.getByText(/installed, on no track: testcube/)).toBeInTheDocument()
  })

  it('renders a track name as text', () => {
    // VelociDrone downloads community tracks, and their names are written by whoever
    // uploaded them.
    render(<Setup state={state({ tracks: [track({ track: xss })] })} log={[]} />)
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
    expect(screen.queryByText(/12:00:00/)).toBeNull()
    rerender(<Setup state={state()} log={['12:00:00  installed']} />)
    // getByText normalises whitespace, and the host pads the timestamp with two spaces.
    expect(screen.getByText(/12:00:00\s+installed/)).toBeInTheDocument()
  })
})
