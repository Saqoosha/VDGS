import { fireEvent, render, screen } from '@testing-library/react'
import { describe, it, expect, vi } from 'vitest'
import Tracks from './Tracks'
import { send } from '../bridge'
import type { SetupState, TrackEntry } from '../types'

vi.mock('../bridge', () => ({ send: vi.fn() }))

const xss = '<img src=x onerror=alert(1)>'

const state = (over: Partial<SetupState>): SetupState =>
  ({
    game: '/games/velocidrone', mod: '0.1.0.0', bundledMod: '0.1.0.0', missing: [],
    ready: true, running: false, busy: null, busyPercent: null, launchArgs: '',
    lanUrl: null, tracks: [], catalog: null, unbound: [], trueLens: null, ...over,
  }) as SetupState

function track(over: Partial<TrackEntry> = {}): TrackEntry {
  return {
    track: 'VDGS FDF', capture: 'fdf', splats: 10, bytes: 1, collision: true,
    captureInstalled: true, converted: true, inGame: true, fromServer: false, ...over,
  }
}

describe('the merged track table', () => {
  it('offers Get for a catalog entry that is not installed', () => {
    render(<Tracks state={state({
      catalog: { url: 'u', error: null, entries: [
        { id: 'a', name: 'Nelson', description: null, author: null, licence: null,
          splats: 1, bytes: 1, installed: false },
      ] },
    })} busy={false} />)
    expect(screen.getByRole('button', { name: /get/i })).toBeInTheDocument()
  })

  it('offers Remove for a track this machine owns', () => {
    render(<Tracks state={state({ tracks: [track({})] })} busy={false} />)
    expect(screen.getByRole('button', { name: /remove/i })).toBeInTheDocument()
  })

  // A track downloaded from the official server belongs to its author. The binding is
  // ours to drop; the track is not ours to delete.
  it('offers only Unbind for a track from the official server', () => {
    render(<Tracks state={state({ tracks: [track({ fromServer: true })] })} busy={false} />)
    expect(screen.getByRole('button', { name: /unbind/i })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /remove/i })).toBeNull()
  })

  // `id` and `installAs` are different namespaces - id is what `get` resolves against,
  // installAs is the capture directory name a binding actually stores. Sending the
  // capture name where `get` expects an id looks fine and silently does nothing, so the
  // row may only offer Get once a catalog entry has actually been resolved.
  it('offers Get for a missing capture once a catalog entry names it as installAs, and sends that entry\'s id', () => {
    render(<Tracks state={state({
      tracks: [track({ captureInstalled: false, capture: 'fdf' })],
      catalog: { url: 'u', error: null, entries: [
        { id: 'fdf-2026-08-24', name: 'FDF', description: null, author: null, licence: null,
          splats: 1, bytes: 1, installed: false, installAs: 'fdf' },
      ] },
    })} busy={false} />)
    fireEvent.click(screen.getByRole('button', { name: /get/i }))
    expect(vi.mocked(send)).toHaveBeenCalledWith('get', 'fdf-2026-08-24')
  })

  it('offers no action for a missing capture when there is no catalog to resolve it against', () => {
    render(<Tracks state={state({
      tracks: [track({ captureInstalled: false, capture: 'fdf' })],
      catalog: null,
    })} busy={false} />)
    expect(screen.getByText(/fdf is not installed/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /get/i })).toBeNull()
  })

  // --- The seven tests carried over from Setup.test.tsx (pre-889c979), against Tracks now ---

  it('renders a track name as text, not markup', () => {
    // VelociDrone downloads community tracks, and their names are written by whoever
    // uploaded them. This is a security test, not a formatting test.
    render(<Tracks state={state({ tracks: [track({ track: xss })] })} busy={false} />)
    expect(screen.getByText(xss)).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })

  it('lists a track with the capture it shows', () => {
    render(<Tracks state={state({
      tracks: [track({ capture: 'FDF-2026-08-24', splats: 1497617 })],
    })} busy={false} />)
    expect(screen.getByText('VDGS FDF')).toBeInTheDocument()
    expect(screen.getByText(/FDF-2026-08-24/)).toBeInTheDocument()
    expect(screen.getByText(/1,497,617 splats/)).toBeInTheDocument()
  })

  it('marks a capture with no collision mesh', () => {
    render(<Tracks state={state({ tracks: [track({ collision: false })] })} busy={false} />)
    expect(screen.getByText(/no collision/)).toBeInTheDocument()
  })

  it('says when a bound capture is not on the machine', () => {
    // Silent otherwise: the track loads and simply shows nothing.
    render(<Tracks state={state({
      tracks: [track({ captureInstalled: false, capture: 'nelson-lod2' })],
    })} busy={false} />)
    expect(screen.getByText(/nelson-lod2 is not installed/)).toBeInTheDocument()
  })

  it('says when a track a binding names is not in the game', () => {
    render(<Tracks state={state({ tracks: [track({ inGame: false })] })} busy={false} />)
    expect(screen.getByText(/not in velocidrone/i)).toBeInTheDocument()
  })

  it('reports captures no track points at', () => {
    render(<Tracks state={state({
      unbound: [{ name: 'testcube', splats: 640, collision: false }],
    })} busy={false} />)
    expect(screen.getByText(/installed, on no track: testcube/)).toBeInTheDocument()
  })

  it('offers to remove a track, and only to unbind a downloaded one', () => {
    // A track from the official server is its author's, not ours to delete off someone's
    // machine - but the binding is ours either way.
    const { rerender } = render(<Tracks state={state({ tracks: [track()] })} busy={false} />)
    expect(screen.getByRole('button', { name: /remove VDGS FDF/i })).toBeInTheDocument()

    rerender(<Tracks state={state({ tracks: [track({ fromServer: true })] })} busy={false} />)
    expect(screen.getByRole('button', { name: /unbind VDGS FDF/i })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /remove VDGS FDF/i })).toBeNull()
  })

  // Unbind only writes bindings.json, a file the game never holds open - unlike Remove,
  // which deletes the row from user11.db while the game keeps that database open. Folding
  // both into one `busy` flag (as this component used to) meant Unbind went dark exactly
  // when someone most wants it: mid-flight, comparing a capture against the track it is
  // bound to.
  it('leaves Unbind enabled while the game runs, unlike Remove', () => {
    const { rerender } = render(
      <Tracks state={state({ running: true, tracks: [track({ fromServer: true })] })} busy={false} />,
    )
    expect(screen.getByRole('button', { name: /unbind VDGS FDF/i })).toBeEnabled()

    rerender(<Tracks state={state({ running: true, tracks: [track()] })} busy={false} />)
    expect(screen.getByRole('button', { name: /remove VDGS FDF/i })).toBeDisabled()
  })
})
