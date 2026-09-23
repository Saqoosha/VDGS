import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
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
    await api.setTransform('a', { scale: 2 })
    const body = JSON.parse(String(fetch.mock.calls[0][1]?.body)) as Record<string, unknown>
    expect(body).toEqual({ splat: 'a', scale: 2 })
  })

  it('setTransform carries x and z too', async () => {
    const fetch = mockFetch()
    await api.setTransform('a', { x: 1, z: 2 })
    const body = JSON.parse(String(fetch.mock.calls[0][1]?.body)) as Record<string, unknown>
    expect(body).toEqual({ splat: 'a', x: 1, z: 2 })
  })

  it('setOrientation posts to /api/transform with up, turn and mirror', async () => {
    const fetch = mockFetch()
    await api.setOrientation('a', { up: 'y', turn: 90, mirror: true })
    expect(fetch.mock.calls[0][0]).toBe('/api/transform')
    const body = JSON.parse(String(fetch.mock.calls[0][1]?.body)) as Record<string, unknown>
    expect(body).toEqual({ splat: 'a', up: 'y', turn: 90, mirror: true })
  })

  it('setLod posts lodDetail and lodBudget to /api/transform', async () => {
    const fetch = mockFetch()
    await api.setLod('a', { lodDetail: 12, lodBudget: 2_000_000 })
    expect(fetch.mock.calls[0][0]).toBe('/api/transform')
    const body = JSON.parse(String(fetch.mock.calls[0][1]?.body)) as Record<string, unknown>
    expect(body).toEqual({ splat: 'a', lodDetail: 12, lodBudget: 2_000_000 })
  })
})

describe('api transport', () => {
  beforeEach(() => {
    vi.resetModules()
    delete (window as any).__TAURI__
  })

  it('uses a relative fetch when the plugin serves the page', async () => {
    const fetchSpy = vi.fn().mockResolvedValue({ ok: true, json: async () => ({}) })
    vi.stubGlobal('fetch', fetchSpy)
    const { load } = await import('./api')
    await load('my-house')
    expect(fetchSpy.mock.calls[0][0]).toBe('/api/load')
  })

  it('goes through the host to an absolute URL when hosted', async () => {
    const hostFetch = vi.fn().mockResolvedValue({ ok: true, json: async () => ({}) })
    ;(window as any).__TAURI__ = {
      core: { invoke: vi.fn() },
      event: { listen: vi.fn().mockResolvedValue(() => {}) },
      http: { fetch: hostFetch },
    }
    const { load } = await import('./api')
    await load('my-house')
    expect(hostFetch.mock.calls[0][0]).toBe('http://127.0.0.1:8777/api/load')
  })
})
