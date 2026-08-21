import { useEffect, useRef } from 'react'

type Splat = {
  x: number
  y: number
  vx: number
  vy: number
  rx: number
  ry: number
  rot: number
  vr: number
  r: number
  g: number
  b: number
  a: number
}

const PALETTE: [number, number, number][] = [
  [255, 118, 64],
  [255, 196, 120],
  [130, 186, 255],
  [186, 140, 255],
  [255, 255, 255],
  [80, 220, 180],
]

function mulberry32(seed: number) {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

function seedSplats(w: number, h: number): Splat[] {
  const rand = mulberry32(0x56444753)
  const out: Splat[] = []
  const n = 96
  for (let i = 0; i < n; i++) {
    const plate = i < 22
    const [r, g, b] = PALETTE[Math.floor(rand() * PALETTE.length)]
    const rx = plate ? 90 + rand() * 220 : 8 + rand() * 42
    const ry = plate ? rx * (0.12 + rand() * 0.28) : rx * (0.35 + rand() * 0.7)
    out.push({
      x: rand() * w,
      y: rand() * h,
      vx: (rand() - 0.5) * (plate ? 0.08 : 0.22),
      vy: (rand() - 0.5) * (plate ? 0.06 : 0.18),
      rx,
      ry,
      rot: rand() * Math.PI,
      vr: (rand() - 0.5) * 0.002,
      r,
      g,
      b,
      a: plate ? 0.045 + rand() * 0.05 : 0.12 + rand() * 0.22,
    })
  }
  return out
}

export function ParticleField() {
  const ref = useRef<HTMLCanvasElement>(null)

  useEffect(() => {
    const canvas = ref.current
    if (!canvas) return
    let ctx: CanvasRenderingContext2D | null = null
    try {
      ctx = canvas.getContext('2d', { alpha: false })
    } catch {
      return
    }
    if (!ctx) return

    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    let splats: Splat[] = []
    let raf = 0
    let running = true

    const resize = () => {
      const dpr = Math.min(window.devicePixelRatio || 1, 2)
      const w = window.innerWidth
      const h = window.innerHeight
      canvas.width = Math.floor(w * dpr)
      canvas.height = Math.floor(h * dpr)
      canvas.style.width = w + 'px'
      canvas.style.height = h + 'px'
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      splats = seedSplats(w, h)
      paint(false)
    }

    const paint = (step: boolean) => {
      const w = window.innerWidth
      const h = window.innerHeight
      ctx.globalCompositeOperation = 'source-over'
      ctx.fillStyle = '#05070c'
      ctx.fillRect(0, 0, w, h)
      ctx.globalCompositeOperation = 'lighter'
      for (const s of splats) {
        if (step) {
          s.x += s.vx
          s.y += s.vy
          s.rot += s.vr
          if (s.x < -s.rx) s.x = w + s.rx
          if (s.x > w + s.rx) s.x = -s.rx
          if (s.y < -s.ry) s.y = h + s.ry
          if (s.y > h + s.ry) s.y = -s.ry
        }
        ctx.save()
        ctx.translate(s.x, s.y)
        ctx.rotate(s.rot)
        ctx.scale(s.rx, s.ry)
        const g = ctx.createRadialGradient(0, 0, 0, 0, 0, 1)
        g.addColorStop(0, `rgba(${s.r},${s.g},${s.b},${s.a})`)
        g.addColorStop(0.45, `rgba(${s.r},${s.g},${s.b},${s.a * 0.35})`)
        g.addColorStop(1, `rgba(${s.r},${s.g},${s.b},0)`)
        ctx.fillStyle = g
        ctx.beginPath()
        ctx.arc(0, 0, 1, 0, Math.PI * 2)
        ctx.fill()
        ctx.restore()
      }
    }

    const tick = () => {
      if (!running) return
      paint(true)
      raf = window.requestAnimationFrame(tick)
    }

    resize()
    window.addEventListener('resize', resize)
    if (!reduce) raf = window.requestAnimationFrame(tick)

    return () => {
      running = false
      window.cancelAnimationFrame(raf)
      window.removeEventListener('resize', resize)
    }
  }, [])

  return (
    <canvas
      ref={ref}
      aria-hidden="true"
      className="pointer-events-none fixed inset-0 -z-10"
    />
  )
}
