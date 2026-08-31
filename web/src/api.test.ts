import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from './api'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function mockFetch(status = 200, body: unknown = { ok: true }) {
  const fetch = vi.fn(async () =>
    new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': 'application/json' },
    }),
  )
  vi.stubGlobal('fetch', fetch)
  return fetch
}

describe('api posts', () => {
  it('always send Content-Type application/json', async () => {
    const fetch = mockFetch()
    await api.unload()
    await api.unbind()
    await api.load('playroom')
    expect(fetch).toHaveBeenCalledTimes(3)
    for (const [, init] of fetch.mock.calls) {
      expect(init?.method).toBe('POST')
      const headers = init?.headers as Record<string, string>
      expect(headers['Content-Type']).toBe('application/json')
    }
  })

  it('unload body is {}', async () => {
    const fetch = mockFetch()
    await api.unload()
    expect(fetch.mock.calls[0][1]?.body).toBe('{}')
  })

  it('unbind without a track is {}', async () => {
    const fetch = mockFetch()
    await api.unbind()
    expect(fetch.mock.calls[0][1]?.body).toBe('{}')
  })

  it('setTransform omits the field that was not passed', async () => {
    const fetch = mockFetch()
    await api.setTransform('a', 2)
    const body = JSON.parse(String(fetch.mock.calls[0][1]?.body)) as Record<string, unknown>
    expect(body).toEqual({ splat: 'a', scale: 2 })
  })
})
