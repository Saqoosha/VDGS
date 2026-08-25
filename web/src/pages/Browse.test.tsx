import { render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Browse from './Browse'

const xss = '<img src=x onerror=alert(1)>'

function serve(body: unknown, ok = true) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve({ ok, status: ok ? 200 : 404, json: () => Promise.resolve(body) })),
  )
}

afterEach(() => vi.unstubAllGlobals())

const scene = {
  id: 'fdf',
  name: 'FDF',
  description: 'An FPV practice field.',
  author: 'Saqoosha',
  licence: 'CC0-1.0',
  splats: 1497617,
  scene: { url: 'https://h/scene/a.zip', bytes: 123_657_212 },
  track: { url: 'https://h/track/a.json', bytes: 3772, name: 'VDGS FDF' },
}

describe('Browse', () => {
  it('lists what is published, with its licence', async () => {
    serve({ formatVersion: 1, scenes: [scene] })
    render(<Browse />)
    expect(await screen.findByText('FDF')).toBeInTheDocument()
    expect(screen.getByText(/1,497,617 splats/)).toBeInTheDocument()
    expect(screen.getByText(/CC0-1\.0/)).toBeInTheDocument()
  })

  it('offers the app, since a browser cannot install anything itself', async () => {
    serve({
      formatVersion: 1,
      scenes: [scene],
      app: { version: '2026.08.25', url: 'https://h/app/c.zip', bytes: 6_061_831 },
    })
    render(<Browse />)
    const download = await screen.findByRole('link', { name: /download the companion/i })
    expect(download).toHaveAttribute('href', 'https://h/app/c.zip')
  })

  it('does not hand out the raw files', async () => {
    // A capture is useless without its track and a binding, and a track file opened in a
    // browser is a wall of JSON. Taking one is the app's job.
    serve({ formatVersion: 1, scenes: [scene] })
    render(<Browse />)
    await screen.findByText('FDF')
    expect(screen.queryByRole('link', { name: /^capture$/i })).toBeNull()
    expect(screen.queryByRole('link', { name: /^track$/i })).toBeNull()
  })

  it('links the loader it installs on your behalf', async () => {
    serve({ formatVersion: 1, scenes: [scene] })
    render(<Browse />)
    const link = await screen.findByRole('link', { name: /BepInEx/i })
    expect(link).toHaveAttribute('href', expect.stringContaining('BepInEx/releases'))
  })

  it('says so when there is no catalog rather than sitting blank', async () => {
    serve(null, false)
    render(<Browse />)
    await waitFor(() => expect(screen.getByText(/catalog\.json: 404/)).toBeInTheDocument())
  })

  it('renders a published name as text', async () => {
    // The catalog is a file on a web server; a mirror of it is not ours.
    serve({ formatVersion: 1, scenes: [{ ...scene, name: xss }] })
    render(<Browse />)
    expect(await screen.findByText(xss)).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })
})
