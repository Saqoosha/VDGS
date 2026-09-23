import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, it, expect, vi } from 'vitest'
import Tracks, { TracksToolbar } from './Tracks'
import { send } from '../bridge'
import type { SetupState, Status, TrackEntry } from '../types'

vi.mock('../bridge', () => ({ send: vi.fn() }))

// Tracks asks the plugin which track is on screen - a different channel from the
// bridge above - so a test can say what the plugin answered without a network fetch.
let pluginState: Status | null = null
let pluginLive = false
vi.mock('../useStatus', () => ({
  useStatus: () => ({ state: pluginState, live: pluginLive, refresh: async () => pluginState }),
}))

// The three props every render needs and no existing test cares about.
const noop = { picked: null, onPickedDone: () => {}, onTweak: () => {}, q: '', onSearch: () => {} }

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
  beforeEach(() => {
    pluginState = null
    pluginLive = false
    vi.mocked(send).mockClear()
  })

  it('offers Get for a catalog entry that is not installed', () => {
    render(<Tracks state={state({
      catalog: { url: 'u', error: null, entries: [
        { id: 'a', name: 'Nelson', description: null, author: null, licence: null,
          splats: 1, bytes: 1, installed: false },
      ] },
    })} busy={false} {...noop} />)
    expect(screen.getByRole('button', { name: /get/i })).toBeInTheDocument()
  })

  it('offers Remove for a track this machine owns', () => {
    render(<Tracks state={state({ tracks: [track({})] })} busy={false} {...noop} />)
    expect(screen.getByRole('button', { name: /remove/i })).toBeInTheDocument()
  })

  // A track downloaded from the official server belongs to its author. The binding is
  // ours to drop; the track is not ours to delete.
  it('offers only Unbind for a track from the official server', () => {
    render(<Tracks state={state({ tracks: [track({ fromServer: true })] })} busy={false} {...noop} />)
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
    })} busy={false} {...noop} />)
    fireEvent.click(screen.getByRole('button', { name: /get/i }))
    expect(vi.mocked(send)).toHaveBeenCalledWith('get', 'fdf-2026-08-24')
  })

  // An update is the same get: the host swaps the folder for the new cut and leaves the
  // binding and placement alone. It shows only when the catalog says the installed
  // folder is older, beside the row's usual action.
  it('offers Update on an installed track when the catalog has a newer cut, and sends the entry\'s id', () => {
    render(<Tracks state={state({
      tracks: [track({ capture: 'fdf' })],
      catalog: { url: 'u', error: null, entries: [
        { id: 'fdf-2026-08-24', name: 'FDF', description: null, author: null, licence: null,
          splats: 1, bytes: 1, installed: true, update: true, installAs: 'fdf' },
      ] },
    })} busy={false} {...noop} />)
    fireEvent.click(screen.getByRole('button', { name: /update/i }))
    expect(vi.mocked(send)).toHaveBeenCalledWith('get', 'fdf-2026-08-24')
    expect(screen.getByRole('button', { name: /remove/i })).toBeInTheDocument()
  })

  // The game box on 2026-09-23: the track was bound by hand to one capture while the
  // catalog installs the same track as another. It listed twice, and a Get lit both rows.
  const r6 = {
    id: 'jdl-2026-r6', name: 'VDGS JDL 2026 R6', description: null, author: null,
    licence: null, splats: 1, bytes: 1, installed: false, update: false,
    installAs: 'JDL-2026-R6', track: 'VDGS JDL 2026 R6',
  }
  const handBound = track({ track: 'VDGS JDL 2026 R6', capture: 'JDL-2026-R6-fix-edit' })

  it('lists a track bound to another capture once, and offers Replace with the catalog cut', () => {
    render(<Tracks state={state({
      tracks: [handBound], catalog: { url: 'u', error: null, entries: [r6] },
    })} busy={false} {...noop} />)
    expect(screen.getAllByText('VDGS JDL 2026 R6')).toHaveLength(1)
    fireEvent.click(screen.getByRole('button', { name: /replace/i }))
    // Not 'get': with the catalog's folder already on disk a get is an update and keeps
    // the old binding, so Replace would download and change nothing.
    expect(vi.mocked(send)).toHaveBeenCalledWith('replace', 'jdl-2026-r6')
    expect(screen.getByRole('button', { name: /remove/i })).toBeInTheDocument()
  })

  it('shows one progress bar, not two, while that Replace downloads', () => {
    render(<Tracks state={state({
      tracks: [handBound], catalog: { url: 'u', error: null, entries: [r6] },
      busy: 'downloading VDGS JDL 2026 R6', busyPercent: 10,
    })} busy={true} {...noop} />)
    expect(screen.getAllByRole('progressbar', { name: /downloading VDGS JDL 2026 R6/i })).toHaveLength(1)
  })

  it('rebinds when Get fetches a missing capture found by track rather than by capture', () => {
    render(<Tracks state={state({
      tracks: [{ ...handBound, captureInstalled: false }],
      catalog: { url: 'u', error: null, entries: [r6] },
    })} busy={false} {...noop} />)
    fireEvent.click(screen.getByRole('button', { name: /^get$/i }))
    expect(vi.mocked(send)).toHaveBeenCalledWith('replace', 'jdl-2026-r6')
  })

  it('offers no Replace when the catalog cut is one of several captures the track is bound to', () => {
    render(<Tracks state={state({
      tracks: [track({ track: 'VDGS JDL 2026 R6', capture: 'JDL-2026-R6 + extra',
        captures: ['JDL-2026-R6', 'extra'] })],
      catalog: { url: 'u', error: null, entries: [{ ...r6, installed: true }] },
    })} busy={false} {...noop} />)
    expect(screen.queryByRole('button', { name: /replace/i })).toBeNull()
    expect(screen.getAllByText('VDGS JDL 2026 R6')).toHaveLength(1)
  })

  it('offers no Replace when the track is bound to the catalog cut itself', () => {
    render(<Tracks state={state({
      tracks: [track({ track: 'VDGS JDL 2026 R6', capture: 'JDL-2026-R6' })],
      catalog: { url: 'u', error: null, entries: [{ ...r6, installed: true }] },
    })} busy={false} {...noop} />)
    expect(screen.queryByRole('button', { name: /replace/i })).toBeNull()
    expect(screen.getAllByText('VDGS JDL 2026 R6')).toHaveLength(1)
  })

  it('offers no Update on an installed track that is current', () => {
    render(<Tracks state={state({
      tracks: [track({ capture: 'fdf' })],
      catalog: { url: 'u', error: null, entries: [
        { id: 'fdf-2026-08-24', name: 'FDF', description: null, author: null, licence: null,
          splats: 1, bytes: 1, installed: true, update: false, installAs: 'fdf' },
      ] },
    })} busy={false} {...noop} />)
    expect(screen.queryByRole('button', { name: /update/i })).toBeNull()
  })

  it('offers no action for a missing capture when there is no catalog to resolve it against', () => {
    render(<Tracks state={state({
      tracks: [track({ captureInstalled: false, capture: 'fdf' })],
      catalog: null,
    })} busy={false} {...noop} />)
    expect(screen.getByText(/fdf is not installed/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /get/i })).toBeNull()
  })

  // --- The seven tests carried over from Setup.test.tsx (pre-889c979), against Tracks now ---

  it('renders a track name as text, not markup', () => {
    // VelociDrone downloads community tracks, and their names are written by whoever
    // uploaded them. This is a security test, not a formatting test.
    render(<Tracks state={state({ tracks: [track({ track: xss })] })} busy={false} {...noop} />)
    expect(screen.getByText(xss)).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })

  it('lists a track with the capture it shows', () => {
    render(<Tracks state={state({
      tracks: [track({ capture: 'FDF-2026-08-24', splats: 1497617 })],
    })} busy={false} {...noop} />)
    expect(screen.getByText('VDGS FDF')).toBeInTheDocument()
    expect(screen.getByText(/FDF-2026-08-24/)).toBeInTheDocument()
    expect(screen.getByText(/1,497,617 splats/)).toBeInTheDocument()
  })

  it('marks a capture with no collision mesh', () => {
    render(<Tracks state={state({ tracks: [track({ collision: false })] })} busy={false} {...noop} />)
    expect(screen.getByText(/no collision/)).toBeInTheDocument()
  })

  it('says when a bound capture is not on the machine', () => {
    // Silent otherwise: the track loads and simply shows nothing.
    render(<Tracks state={state({
      tracks: [track({ captureInstalled: false, capture: 'nelson-lod2' })],
    })} busy={false} {...noop} />)
    expect(screen.getByText(/nelson-lod2 is not installed/)).toBeInTheDocument()
  })

  it('says when a track a binding names is not in the game', () => {
    render(<Tracks state={state({ tracks: [track({ inGame: false })] })} busy={false} {...noop} />)
    expect(screen.getByText(/not in velocidrone/i)).toBeInTheDocument()
  })

  // Add track cannot leave one of these behind any more, but a file copied into vdgs/
  // by hand still can, and nothing else on the page would ever mention it.
  it('reports captures no track points at, and offers to remove each', () => {
    render(<Tracks state={state({
      unbound: [{ name: 'testcube', splats: 640, collision: false }],
    })} busy={false} {...noop} />)
    expect(screen.getByText(/installed, on no track:/)).toBeInTheDocument()
    expect(screen.getByText('testcube')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /remove testcube/i }))
    expect(send).toHaveBeenCalledWith('removeCapture', 'testcube')
  })

  it('offers to remove a track, and only to unbind a downloaded one', () => {
    // A track from the official server is its author's, not ours to delete off someone's
    // machine - but the binding is ours either way.
    const { rerender } = render(<Tracks state={state({ tracks: [track()] })} busy={false} {...noop} />)
    expect(screen.getByRole('button', { name: /remove VDGS FDF/i })).toBeInTheDocument()

    rerender(<Tracks state={state({ tracks: [track({ fromServer: true })] })} busy={false} {...noop} />)
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
      <Tracks state={state({ running: true, tracks: [track({ fromServer: true })] })} busy={false} {...noop} />,
    )
    expect(screen.getByRole('button', { name: /unbind VDGS FDF/i })).toBeEnabled()

    rerender(<Tracks state={state({ running: true, tracks: [track()] })} busy={false} {...noop} />)
    expect(screen.getByRole('button', { name: /remove VDGS FDF/i })).toBeDisabled()
  })

  // Add track is file -> name -> create. The button only asks the host for the file;
  // the name dialog appears on `picked`, and only Create sends anything that writes.
  it('asks the host to pick a .ply, and nothing else, on Add track', () => {
    render(<TracksToolbar state={state({})} busy={false} picked={null} />)
    fireEvent.click(screen.getByRole('button', { name: /add track/i }))
    expect(send).toHaveBeenCalledTimes(1)
    expect(send).toHaveBeenCalledWith('pickPly')
  })

  it('shows the name dialog for a picked file, defaulting to the file stem', () => {
    render(
      <Tracks
        state={state({})}
        busy={false}
        {...noop}
        picked={{ path: '/d/himeji-lod2.ply', stem: 'himeji-lod2' }}
      />,
    )
    expect(screen.getByRole('textbox', { name: /track name/i })).toHaveValue('VDGS himeji-lod2')
  })

  // One pick at a time: the button waits until the name dialog is settled.
  it('holds Add track while a pick is waiting for its name', () => {
    render(
      <TracksToolbar
        state={state({})}
        busy={false}
        picked={{ path: '/d/himeji-lod2.ply', stem: 'himeji-lod2' }}
      />,
    )
    expect(screen.getByRole('button', { name: /add track/i })).toBeDisabled()
  })

  it('sends the path and the typed name together on Create, then clears the pick', async () => {
    const onPickedDone = vi.fn()
    render(
      <Tracks
        state={state({})}
        busy={false}
        {...noop}
        onPickedDone={onPickedDone}
        picked={{ path: '/d/himeji-lod2.ply', stem: 'himeji-lod2' }}
      />,
    )
    const name = screen.getByRole('textbox', { name: /track name/i })
    await userEvent.clear(name)
    await userEvent.type(name, 'Himeji Castle')
    fireEvent.click(screen.getByRole('button', { name: /^create$/i }))
    expect(send).toHaveBeenCalledWith('addTrack', undefined, {
      path: '/d/himeji-lod2.ply',
      name: 'Himeji Castle',
    })
    expect(onPickedDone).toHaveBeenCalled()
  })

  it('writes nothing on Cancel', () => {
    const onPickedDone = vi.fn()
    render(
      <Tracks
        state={state({})}
        busy={false}
        {...noop}
        onPickedDone={onPickedDone}
        picked={{ path: '/d/himeji-lod2.ply', stem: 'himeji-lod2' }}
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: /cancel/i }))
    expect(send).not.toHaveBeenCalledWith('addTrack', expect.anything(), expect.anything())
    expect(onPickedDone).toHaveBeenCalled()
  })

  // Tweak goes over the plugin's HTTP API and only reaches the capture on screen, so it
  // is live on exactly the row the plugin says it is showing - and visible but off on the
  // others, so the way in is learnable.
  it('offers Tweak only on the track the plugin is showing', () => {
    const onTweak = vi.fn()
    pluginLive = true
    pluginState = { track: 'VDGS FDF', loaded: ['fdf'], available: [], bindings: {} }
    render(
      <Tracks
        state={state({ running: true, tracks: [track(), track({ track: 'VDGS Other', capture: 'other' })] })}
        busy={false}
        {...noop}
        onTweak={onTweak}
      />,
    )
    expect(screen.getByRole('button', { name: /tweak VDGS FDF/i })).toBeEnabled()
    expect(screen.getByRole('button', { name: /tweak VDGS Other/i })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: /tweak VDGS FDF/i }))
    expect(onTweak).toHaveBeenCalledWith('VDGS FDF')
  })

  it('keeps Tweak off while the plugin is not answering', () => {
    pluginLive = false
    pluginState = { track: 'VDGS FDF', loaded: ['fdf'], available: [], bindings: {} }
    render(<Tracks state={state({ tracks: [track()] })} busy={false} {...noop} />)
    expect(screen.getByRole('button', { name: /tweak VDGS FDF/i })).toBeDisabled()
  })

  // Where the button was pressed, not the corner of the window (#12) - this was on the
  // page until the catalog and the track list were folded together, and went missing.
  // A download shows in the row that started it, in place of its Get button, and the
  // head-of-table bar stays out of the way. "downloading X" and "installing X" are the
  // two phrases the host uses, matched exactly so "FDF" does not light up for "FDF night".
  it('shows a download as a ring on its own row, not at the head of the table', () => {
    const entry = {
      id: 'jdl', name: 'VDGS JDL 2026 R5', description: null, author: null, licence: null,
      splats: 1, bytes: 1, installed: false, installAs: 'JDL',
    }
    const other = { ...entry, id: 'fdf', name: 'VDGS FDF', installAs: 'FDF' }
    render(
      <Tracks
        state={state({
          busy: 'downloading VDGS JDL 2026 R5', busyPercent: 43,
          catalog: { url: 'x', error: null, entries: [entry, other] },
        })}
        busy={true}
        {...noop}
      />,
    )
    const ring = screen.getByRole('progressbar', { name: /downloading VDGS JDL 2026 R5/i })
    expect(ring).toHaveAttribute('aria-valuenow', '43')
    expect(ring.closest('li')).toHaveTextContent('VDGS JDL 2026 R5')
    expect(screen.getAllByRole('progressbar')).toHaveLength(1)
    // The row that is downloading has no Get to press twice; the other keeps its own.
    expect(screen.getAllByRole('button', { name: /^get$/i })).toHaveLength(1)
  })

  it('lists installed tracks before available ones, and narrows to either', () => {
    const entry = {
      id: 'aaa', name: 'AAA from the catalog', description: null, author: null, licence: null,
      splats: 1, bytes: 1, installed: false, installAs: 'AAA',
    }
    render(
      <Tracks
        state={state({ tracks: [track({ track: 'ZZZ mine' })], catalog: { url: 'x', error: null, entries: [entry] } })}
        busy={false}
        {...noop}
      />,
    )
    const names = () => screen.getAllByRole('listitem').map((li) => li.textContent ?? '')
    // Installed first even though it sorts after by name.
    expect(names()[0]).toContain('ZZZ mine')
    expect(names()[1]).toContain('AAA from the catalog')

    fireEvent.click(screen.getByRole('radio', { name: /^installed$/i }))
    expect(names()).toHaveLength(1)
    expect(names()[0]).toContain('ZZZ mine')

    fireEvent.click(screen.getByRole('radio', { name: /^available$/i }))
    expect(names()).toHaveLength(1)
    expect(names()[0]).toContain('AAA from the catalog')
  })

  // A track row fetching its missing capture goes through the catalog entry, and the host
  // names that job after the entry, not the track.
  it('shows a track row download fetched through the catalog on that row', () => {
    const entry = {
      id: 'nelson', name: 'Nelson', description: null, author: null, licence: null,
      splats: 1, bytes: 1, installed: false, installAs: 'nelson-lod2',
    }
    render(
      <Tracks
        state={state({
          busy: 'downloading Nelson', busyPercent: 12,
          tracks: [track({ track: 'VDGS Nelson', capture: 'nelson-lod2', captureInstalled: false })],
          catalog: { url: 'x', error: null, entries: [entry] },
        })}
        busy={true}
        {...noop}
      />,
    )
    const ring = screen.getByRole('progressbar', { name: /downloading Nelson/i })
    expect(ring.closest('li')).toHaveTextContent('VDGS Nelson')
    expect(screen.getAllByRole('progressbar')).toHaveLength(1)
  })

  // A filter that hides the busy row must not also hide the progress: the bar at the
  // head of the table takes over for a row that is not on screen.
  it('falls back to the head-of-table bar when the busy row is filtered out', () => {
    const entry = {
      id: 'jdl', name: 'VDGS JDL', description: null, author: null, licence: null,
      splats: 1, bytes: 1, installed: false, installAs: 'JDL',
    }
    render(
      <Tracks
        state={state({
          busy: 'downloading VDGS JDL', busyPercent: 30,
          tracks: [track()],
          catalog: { url: 'x', error: null, entries: [entry] },
        })}
        busy={true}
        {...noop}
      />,
    )
    fireEvent.click(screen.getByRole('radio', { name: /^installed$/i }))
    expect(screen.getByRole('progressbar', { name: /downloading VDGS JDL/i })).toBeInTheDocument()
  })

  it('shows a removal as a turning ring on its own row', () => {
    render(
      <Tracks
        state={state({ busy: 'removing VDGS FDF', busyPercent: null, tracks: [track()] })}
        busy={true}
        {...noop}
      />,
    )
    const ring = screen.getByRole('progressbar', { name: /removing VDGS FDF/i })
    expect(ring).not.toHaveAttribute('aria-valuenow')
    expect(ring.closest('li')).toHaveTextContent('VDGS FDF')
    expect(screen.queryByRole('button', { name: /remove VDGS FDF/i })).toBeNull()
  })

  it('shows the progress bar in the table while a job runs', () => {
    render(
      <Tracks state={state({ busy: 'fetching nelson', busyPercent: 43 })} busy={true} {...noop} />,
    )
    expect(screen.getByRole('progressbar', { name: /fetching nelson/i })).toHaveAttribute(
      'aria-valuenow',
      '43',
    )
  })
})
