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
  // The instructions are the only part that carries a translation - the list above them
  // is names, numbers and licences, which read the same either way.
  it('says how to do it in Japanese when asked', async () => {
    serve({
      formatVersion: 1,
      scenes: [scene],
      app: { windows: { version: '1', url: 'https://h/a.zip', bytes: 10 } },
    })
    render(<Browse lang="ja" />)
    await waitFor(() => expect(screen.getByText(/使い方/)).toBeInTheDocument())
    expect(screen.getByText(/companion をダウンロードして解凍し/)).toBeInTheDocument()
    // The half that was missing: a scan only shows on the track it is bound to, so
    // without this the reader flies somewhere else and sees an empty sky.
    expect(screen.getByText(/VelociDrone でそのコースを名前で選ぶ/)).toBeInTheDocument()
    expect(screen.getByText(/スキャンとトラックデータをダウンロード/)).toBeInTheDocument()
    // The first wall anyone meets, and the one that shows only "Don't run" until More
    // info is pressed. Left unsaid, most people stop here.
    expect(screen.getByText(/Windows が警告を出したら/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'companion をダウンロード Windows' })).toBeInTheDocument()
    // The app's own buttons are English in both languages, because the app is.
    expect(screen.getByText('Install mod')).toBeInTheDocument()
    expect(screen.getByText('Fly')).toBeInTheDocument()
  })

  it('says the same thing in English', async () => {
    serve({
      formatVersion: 1,
      scenes: [scene],
      app: { windows: { version: '1', url: 'https://h/a.zip', bytes: 10 } },
    })
    render(<Browse lang="en" />)
    await waitFor(() =>
      expect(screen.getByText(/Download the companion, unzip it/)).toBeInTheDocument(),
    )
    expect(screen.getByText(/pick that track by name/)).toBeInTheDocument()
    expect(screen.getByText(/Download the scan and its track/)).toBeInTheDocument()
    expect(screen.getByText(/Windows will warn about an unrecognised app/)).toBeInTheDocument()
    expect(screen.queryByText(/使い方/)).not.toBeInTheDocument()
  })

  // A section that declares itself Japanese hands everything inside it to a Japanese
  // voice, including the English button names it deliberately did not translate.
  it('marks the app\'s own button names as English inside the Japanese section', async () => {
    serve({
      formatVersion: 1,
      scenes: [scene],
      app: { windows: { version: '1', url: 'https://h/a.zip', bytes: 10 } },
    })
    const { container } = render(<Browse lang="ja" />)
    await waitFor(() => expect(screen.getByText('使い方')).toBeInTheDocument())
    for (const name of [
      'VDGS.exe',
      'More info',
      'Run anyway',
      'VDGS Companion',
      'Applications',
      'Install mod',
      'Change',
      '02 get',
      'Fly',
    ])
      expect(screen.getByText(name).closest('[lang]')?.getAttribute('lang'), name).toBe('en')
    expect(container.querySelectorAll('b[lang="en"]')).toHaveLength(9)
  })

  // Latin micro-caps tracking spaces kanji apart; this is the page's biggest button.
  it('does not letter-space the Japanese download button', async () => {
    serve({
      formatVersion: 1,
      scenes: [scene],
      app: { windows: { version: '1', url: 'https://h/a.zip', bytes: 10 } },
    })
    const { rerender } = render(<Browse lang="ja" />)
    await waitFor(() =>
      expect(screen.getByRole('link', { name: 'companion をダウンロード Windows' })).toBeInTheDocument(),
    )
    expect(screen.getByRole('link', { name: 'companion をダウンロード Windows' }).className).not.toMatch(
      /tracking-/,
    )
    rerender(<Browse lang="en" />)
    await waitFor(() =>
      expect(screen.getByRole('link', { name: 'Download the companion Windows' })).toBeInTheDocument(),
    )
    expect(screen.getByRole('link', { name: 'Download the companion Windows' }).className).toMatch(
      /tracking-/,
    )
  })

  // "captures" is the word this project uses among itself and says nothing to a visitor.
  it('calls them scans, not captures', async () => {
    serve({ formatVersion: 1, scenes: [scene] })
    render(<Browse lang="en" />)
    await waitFor(() => expect(screen.getByText('scans')).toBeInTheDocument())
    expect(screen.queryByText('captures')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Search scans')).toBeInTheDocument()
  })

  it('lists what is published, with its licence', async () => {
    serve({ formatVersion: 1, scenes: [scene] })
    render(<Browse lang="en" />)
    expect(await screen.findByText('FDF')).toBeInTheDocument()
    expect(screen.getByText(/1,497,617 splats/)).toBeInTheDocument()
    expect(screen.getByText(/CC0-1\.0/)).toBeInTheDocument()
  })

  it('offers the app, since a browser cannot install anything itself', async () => {
    serve({
      formatVersion: 1,
      scenes: [scene],
      app: {
        windows: { version: '2026.08.25', url: 'https://h/app/c.zip', bytes: 6_061_831 },
      },
    })
    render(<Browse lang="en" />)
    const download = await screen.findByRole('link', { name: /download the companion windows/i })
    expect(download).toHaveAttribute('href', 'https://h/app/c.zip')
  })

  it('does not hand out the raw files', async () => {
    // A capture is useless without its track and a binding, and a track file opened in a
    // browser is a wall of JSON. Taking one is the app's job.
    serve({ formatVersion: 1, scenes: [scene] })
    render(<Browse lang="en" />)
    await screen.findByText('FDF')
    expect(screen.queryByRole('link', { name: /^capture$/i })).toBeNull()
    expect(screen.queryByRole('link', { name: /^track$/i })).toBeNull()
  })

  it('links the loader it installs on your behalf, one build per platform', async () => {
    serve({ formatVersion: 1, scenes: [scene] })
    render(<Browse lang="en" />)
    const win = await screen.findByRole('link', { name: /win_x64/i })
    expect(win).toHaveAttribute('href', expect.stringContaining('BepInEx/BepInEx/releases'))
    // The macOS one is a fork: the official build's preloader dies on arm64, so linking
    // the upstream release there would send someone to a loader that cannot work.
    const mac = screen.getByRole('link', { name: /macos_universal/i })
    expect(mac).toHaveAttribute('href', expect.stringContaining('Saqoosha/BepInEx/releases'))
  })

  it('says so when there is no catalog rather than sitting blank', async () => {
    serve(null, false)
    render(<Browse lang="en" />)
    await waitFor(() => expect(screen.getByText(/catalog\.json: 404/)).toBeInTheDocument())
  })

  it('renders a published name as text', async () => {
    // The catalog is a file on a web server; a mirror of it is not ours.
    serve({ formatVersion: 1, scenes: [{ ...scene, name: xss }] })
    render(<Browse lang="en" />)
    expect(await screen.findByText(xss)).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })
})
