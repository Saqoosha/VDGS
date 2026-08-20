import { useState } from 'react'
import * as api from '../api'
import { runExclusive } from '../busy'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { filterScenes } from '../search'
import { useStatusContext } from '../status-context'
import type { Scene } from '../types'

export default function Library() {
  const { state, refresh } = useStatusContext()
  const [q, setQ] = useState('')
  const [flash, setFlash] = useState('')
  const available = state?.available ?? []
  const shown = filterScenes(available, q)

  const show = async (name: string) => {
    try {
      await runExclusive(async () => {
        await api.load(name)
        await refresh()
      })
    } catch (e) {
      setFlash(e instanceof Error ? e.message : 'failed')
      window.setTimeout(() => setFlash(''), 2000)
    }
  }

  return (
    <div>
      <label className="flex items-end gap-4 border-b-2 border-foreground/80 pb-1.5">
        <span className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
          find
        </span>
        <Input
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder="name…"
          aria-label="Search scenes"
          className="h-8 border-0 bg-transparent px-0 font-serif text-xl shadow-none focus-visible:ring-0"
        />
      </label>
      {flash ? (
        <p className="mt-3 font-mono text-[11px] tracking-[0.14em] text-live uppercase" role="status">
          {flash}
        </p>
      ) : null}
      {!available.length ? (
        <p className="mt-10 font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
          nothing in &lt;game&gt;/vdgs/
        </p>
      ) : shown.length === 0 ? (
        <p className="mt-10 font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
          no matches
        </p>
      ) : (
        <ol className="mt-1">
          {shown.map((s, i) => (
            <SceneRow
              key={s.name}
              index={String(i + 1).padStart(2, '0')}
              scene={s}
              onShow={() => void show(s.name)}
            />
          ))}
        </ol>
      )}
    </div>
  )
}

function SceneRow({
  index,
  scene,
  onShow,
}: {
  index: string
  scene: Scene
  onShow: () => void
}) {
  const formats = [scene.posFormat, scene.scaleFormat, scene.colorFormat, scene.shFormat]
    .filter(Boolean)
    .join(' · ')

  return (
    <li className="grid grid-cols-[2.25rem_minmax(0,1fr)_auto] items-start gap-3 border-b border-rule/80 py-4">
      <span className="pt-1 font-mono text-[11px] text-muted-foreground">{index}</span>
      <div className="min-w-0">
        <div className="flex flex-wrap items-baseline gap-3">
          <p className="font-serif text-[1.65rem] leading-tight font-light">{scene.name}</p>
          {scene.shown ? (
            <span className="font-mono text-[10px] tracking-[0.2em] text-signal uppercase">
              shown
            </span>
          ) : null}
        </div>
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
          {scene.splats ? scene.splats.toLocaleString() : '—'} splats
          <span className="mx-2 text-rule">/</span>
          {scene.kind}
          <span className="mx-2 text-rule">/</span>
          {formats || '—'}
          <span className="mx-2 text-rule">/</span>
          {formatBytes(scene.bytes) ?? '—'}
          {scene.hasCollision ? ' · collision' : ''}
        </p>
      </div>
      <Button variant={scene.shown ? 'ghost' : 'default'} disabled={scene.shown} onClick={onShow}>
        {scene.shown ? 'Shown' : 'Show'}
      </Button>
    </li>
  )
}

function formatBytes(n?: number): string | null {
  if (n == null || n <= 0) return null
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`
}
