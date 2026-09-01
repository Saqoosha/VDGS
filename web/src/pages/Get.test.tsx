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
    needsMod: null,
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

  // Refusing without saying why is the same as being broken: the fix is on the setup
  // page, and nothing about a greyed-out Get points there.
  it('says which mod a capture needs before refusing to fetch it', () => {
    render(
      <Get
        state={state({
          catalog: { url: 'u', error: null, entries: [entry({ needsMod: '2026.09.01' })] },
        })}
      />,
    )
    expect(screen.getByText(/needs mod 2026\.09\.01 or newer/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^get$/i })).toBeDisabled()
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
