import { waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

/**
 * Each page has an entry module whose only job is to mount the app, and nothing else
 * imports it - so a broken one type-checks, builds, and ships a blank page.
 *
 * This happened: site.tsx and Site.tsx are the same file on a case-insensitive disk, and
 * writing both left only the component. Every other test still passed.
 */
async function mounts(load: () => Promise<unknown>) {
  document.body.innerHTML = '<div id="root"></div>'
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ scenes: [] }) })),
  )
  await load()
  // createRoot().render() schedules; it does not paint before it returns.
  const root = document.getElementById('root')!
  await waitFor(() => expect(root.childNodes.length).toBeGreaterThan(0))
  return root.childNodes.length > 0
}

describe('entry points', () => {
  it('the public page mounts', async () => {
    expect(await mounts(() => import('./site'))).toBe(true)
  })

  it('the companion window mounts', async () => {
    expect(await mounts(() => import('./companion'))).toBe(true)
  })

  it('the in-game UI mounts', async () => {
    expect(await mounts(() => import('./main'))).toBe(true)
  })
})
