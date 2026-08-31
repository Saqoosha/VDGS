import type { IncomingMessage } from 'node:http'
import type { Plugin } from 'vite'
import type { Scene, Status } from './src/types.ts'

function scene(over: Partial<Scene> = {}): Scene {
  return {
    name: 'playroom',
    source: 'local',
    kind: 'converted',
    splats: 1_916_379,
    posFormat: 'Norm16',
    scaleFormat: 'Norm16',
    colorFormat: 'Float16x4',
    shFormat: 'Norm11',
    bytes: 161_000_000,
    hasCollision: true,
    shown: false,
    scale: 1,
    y: 0,
    backdrop: false,
    collision: true,
    collisionView: 'off',
    ...over,
  }
}

function sample(): Status {
  const playroom = scene({ shown: true })
  const drjohnson = scene({
    name: 'drjohnson-high',
    splats: 3_177_554,
    bytes: 260_000_000,
    shown: false,
  })
  const utlida = scene({
    name: 'utlida-full-s5',
    splats: 4_001_829,
    bytes: 340_000_000,
    shown: false,
    hasCollision: true,
  })
  return {
    track: 'Empty Scene Day',
    loaded: ['playroom'],
    available: [playroom, drjohnson, utlida],
    bindings: { 'Empty Scene Day': ['playroom'] },
  }
}

function readJson(req: IncomingMessage): Promise<Record<string, unknown>> {
  return new Promise((resolve) => {
    const chunks: Buffer[] = []
    req.on('data', (c: Buffer) => chunks.push(c))
    req.on('end', () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}') as Record<string, unknown>)
      } catch {
        resolve({})
      }
    })
  })
}

function apply(status: Status, url: string, body: Record<string, unknown>): Status {
  const next: Status = {
    ...status,
    loaded: [...status.loaded],
    available: status.available.map((s: Scene) => ({ ...s })),
    bindings: { ...status.bindings },
  }
  const named = (n: unknown) => next.available.find((s: Scene) => s.name === n)

  if (url === '/api/load') {
    const splat = String(body.splat ?? '')
    next.loaded = splat ? [splat] : []
    for (const s of next.available) s.shown = s.name === splat
  } else if (url === '/api/unload') {
    next.loaded = []
    for (const s of next.available) s.shown = false
  } else if (url === '/api/bind' && next.track) {
    const splats = Array.isArray(body.splats) ? body.splats.map(String) : next.loaded
    next.bindings = { ...next.bindings, [next.track]: splats }
  } else if (url === '/api/unbind') {
    const track = typeof body.track === 'string' ? body.track : next.track
    if (track) {
      const rest = { ...next.bindings }
      delete rest[track]
      next.bindings = rest
    }
  } else if (url === '/api/backdrop') {
    const s = named(body.splat)
    if (s) s.backdrop = body.on === true
  } else if (url === '/api/collision') {
    const s = named(body.splat)
    if (s) s.collision = body.on === true
  } else if (url === '/api/collisionview') {
    const s = named(body.splat)
    if (s && (body.mode === 'off' || body.mode === 'solid' || body.mode === 'wire')) {
      s.collisionView = body.mode
    }
  } else if (url === '/api/transform') {
    const s = named(body.splat)
    if (s) {
      if (typeof body.scale === 'number') s.scale = body.scale
      if (typeof body.y === 'number') s.y = body.y
    }
  }
  return next
}

export function mockApi(): Plugin {
  let status = sample()
  return {
    name: 'vdgs-mock-api',
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const url = req.url?.split('?')[0] ?? ''
        if (req.method === 'GET' && url === '/api/status') {
          res.setHeader('Content-Type', 'application/json')
          res.end(JSON.stringify(status))
          return
        }
        if (req.method === 'POST' && url.startsWith('/api/')) {
          void readJson(req).then((body) => {
            status = apply(status, url, body)
            res.setHeader('Content-Type', 'application/json')
            res.end('{}')
          })
          return
        }
        next()
      })
    },
  }
}
