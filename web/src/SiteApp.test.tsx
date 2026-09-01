import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SiteApp from './SiteApp'

/**
 * The switch itself, not the page it switches.
 *
 * Nothing rendered SiteApp before this, so the wiring between the buttons, the stored
 * choice and the section's lang attribute was carried entirely by reading it - the same
 * blind spot that once let an entry point vanish while every test stayed green.
 */
function serve(body: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) })),
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  localStorage.clear()
})

const empty = { formatVersion: 1, scenes: [] }

describe('the public page', () => {
  it('opens in the browser language when nothing has been chosen', async () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('ja-JP')
    serve(empty)
    render(<SiteApp />)
    await waitFor(() => expect(screen.getByText('使い方')).toBeInTheDocument())
  })

  it('switches, remembers, and says which is on', async () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('en-GB')
    serve(empty)
    render(<SiteApp />)
    await waitFor(() => expect(screen.getByText('how')).toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: 'ja' }))
    expect(screen.getByText('使い方')).toBeInTheDocument()
    expect(localStorage.getItem('vdgs.lang')).toBe('ja')
    expect(screen.getByRole('button', { name: 'ja' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'en' })).toHaveAttribute('aria-pressed', 'false')

    await userEvent.click(screen.getByRole('button', { name: 'en' }))
    expect(screen.getByText('how')).toBeInTheDocument()
    expect(localStorage.getItem('vdgs.lang')).toBe('en')
  })

  // Only the instructions are translated, so declaring the whole document Japanese would
  // hand a speech synthesizer the scan names and licence ids to read as Japanese.
  it('marks the translated section rather than the whole document', async () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('ja-JP')
    serve(empty)
    const { container } = render(<SiteApp />)
    await waitFor(() => expect(screen.getByText('使い方')).toBeInTheDocument())
    expect(container.querySelector('section[lang="ja"]')).not.toBeNull()
    expect(document.documentElement.lang).not.toBe('ja')
  })

  // Both names are on screen at once: a reader who cannot read the current language
  // cannot read a single toggle labelled in it.
  it('shows both languages, not a toggle naming the other', async () => {
    serve(empty)
    render(<SiteApp />)
    expect(screen.getByRole('button', { name: 'en' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'ja' })).toBeInTheDocument()
  })
})
