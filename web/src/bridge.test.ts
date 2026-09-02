import { describe, it, expect, vi, beforeEach } from 'vitest'

describe('bridge transports', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  it('uses the Tauri transport when __TAURI__ is present', async () => {
    const invoke = vi.fn()
    const listen = vi.fn().mockResolvedValue(() => {})
    ;(window as any).__TAURI__ = { core: { invoke }, event: { listen } }
    const { send, subscribe, hosted } = await import('./bridge')
    expect(hosted).toBe(true)
    send('get', 'fdf-2026-08-22')
    expect(invoke).toHaveBeenCalledWith('dispatch', { cmd: 'get', id: 'fdf-2026-08-22' })
    const fn = vi.fn()
    subscribe(fn)
    expect(listen).toHaveBeenCalledWith('push', expect.any(Function))
    delete (window as any).__TAURI__
  })
})

describe('the first command waits for the listener', () => {
  it('does not invoke before listen has resolved', async () => {
    vi.resetModules()
    let release: (u: () => void) => void = () => {}
    const invoke = vi.fn()
    const listen = vi.fn().mockReturnValue(new Promise<() => void>((r) => { release = r }))
    ;(window as any).__TAURI__ = { core: { invoke }, event: { listen } }
    const { send, subscribe } = await import('./bridge')
    subscribe(() => {})
    send('refresh')
    await Promise.resolve()
    expect(invoke).not.toHaveBeenCalled()
    release(() => {})
    await new Promise((r) => setTimeout(r, 0))
    expect(invoke).toHaveBeenCalledWith('dispatch', { cmd: 'refresh', id: null })
    delete (window as any).__TAURI__
  })
})
