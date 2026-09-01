import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import Get from './Get'
import type { CatalogEntry, SetupState } from '../types'

vi.mock('../bridge', () => ({ send: vi.fn() }))

const xss = '<img src=x onerror=alert(1)>'

function entry(over: Partial<CatalogEntry> = {}): CatalogEntry {
  return {
    id: 'fdf',
    name: 'FDF',
    description: 'An FPV practice field.',
    author: 'Saqoosha',
    licence: 'CC0-1.0',
    splats: 1497617,
    bytes: 123_657_212,
    installed: false,
    ...over,
  }
}

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
    launchArgs: '-force-d3d12',
    tracks: [],
    unbound: [],
    catalog: { url: 'https://vdgs.saqoo.sh/catalog.json', error: null, entries: [] },
    ...over,
  }
}

describe('Get', () => {
  // The masthead has carried this since the beginning, at ten pixels in the corner of the
  // window, and a capture takes the better part of a minute. Nobody watches the corner.
  it('shows how far along a download is, where the button was pressed', () => {
    render(
      <Get
        state={state({
          busy: 'downloading FDF',
          busyPercent: 42,
          catalog: { url: 'u', error: null, entries: [entry()] },
        })}
      />,
    )
    expect(screen.getByText(/downloading FDF/i)).toBeInTheDocument()
    expect(screen.getByText('42%')).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '42')
  })

  // Unpacking and importing a track report nothing, and a made-up number would be worse
  // than admitting that.
  it('moves without claiming a number when there is none', () => {
    render(
      <Get
        state={state({
          busy: 'installing FDF',
          busyPercent: null,
          catalog: { url: 'u', error: null, entries: [entry()] },
        })}
      />,
    )
    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
    expect(screen.queryByText('%')).not.toBeInTheDocument()
  })

  it('says nothing when nothing is happening', () => {
    render(<Get state={state({ catalog: { url: 'u', error: null, entries: [entry()] } })} />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  // A second press cannot start a second download - the host drops it - so the page does
  // not offer one either.
  it('will not offer another capture while one is arriving', () => {
    render(
      <Get
        state={state({
          busy: 'downloading FDF',
          busyPercent: 10,
          catalog: { url: 'u', error: null, entries: [entry({ id: 'other', name: 'Other' })] },
        })}
      />,
    )
    expect(screen.getByRole('button', { name: /^get$/i })).toBeDisabled()
  })

  it('shows what a capture costs before it is fetched', () => {
    render(<Get state={state({ catalog: { url: 'u', error: null, entries: [entry()] } })} />)
    expect(screen.getByText('FDF')).toBeInTheDocument()
    expect(screen.getByText(/1,497,617 splats/)).toBeInTheDocument()
    expect(screen.getByText(/117\.9 MB/)).toBeInTheDocument()
  })

  it('names the licence, which nobody can work out by looking', () => {
    render(<Get state={state({ catalog: { url: 'u', error: null, entries: [entry()] } })} />)
    expect(screen.getByText(/CC0-1\.0/)).toBeInTheDocument()
  })

  it('will not offer to fetch what is already here', () => {
    render(
      <Get state={state({ catalog: { url: 'u', error: null, entries: [entry({ installed: true })] } })} />,
    )
    expect(screen.getByRole('button', { name: /installed/i })).toBeDisabled()
  })

  it('will not start a download while something else is running', () => {
    render(
      <Get
        state={state({
          busy: 'installing the mod',
          catalog: { url: 'u', error: null, entries: [entry()] },
        })}
      />,
    )
    expect(screen.getByRole('button', { name: /^get$/i })).toBeDisabled()
  })

  it('says why the list is empty when it is empty for a reason', () => {
    // Nothing published yet and no network look the same from here, and both are worth
    // more than a blank panel.
    render(
      <Get state={state({ catalog: { url: 'u', error: 'could not read u - 404', entries: [] } })} />,
    )
    expect(screen.getByText(/could not read u - 404/)).toBeInTheDocument()
  })

  it('renders a published name as text', () => {
    // The catalog is fetched from a web server; its contents are not ours.
    render(
      <Get state={state({ catalog: { url: 'u', error: null, entries: [entry({ name: xss })] } })} />,
    )
    expect(screen.getByText(xss)).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })
})
