import { useState } from 'react'
import * as api from '../api'
import { runExclusive } from '../busy'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
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
    <div className="flex flex-col gap-5">
      <Input
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder="Search scenes"
        aria-label="Search scenes"
      />
      {flash ? (
        <p className="text-sm text-emerald-700" role="status">
          {flash}
        </p>
      ) : null}
      {!available.length ? (
        <p className="text-muted-foreground">nothing in &lt;game&gt;/vdgs/</p>
      ) : shown.length === 0 ? (
        <p className="text-muted-foreground">no matches</p>
      ) : (
        shown.map((s) => (
          <SceneCard key={s.name} scene={s} onShow={() => void show(s.name)} />
        ))
      )}
    </div>
  )
}

function SceneCard({ scene, onShow }: { scene: Scene; onShow: () => void }) {
  const formats = [scene.posFormat, scene.scaleFormat, scene.colorFormat, scene.shFormat]
    .filter(Boolean)
    .join(' / ')
  const parts = [
    scene.splats ? scene.splats.toLocaleString() + ' splats' : null,
    scene.kind,
    formats || null,
    formatBytes(scene.bytes),
    scene.hasCollision ? 'collision' : null,
  ].filter(Boolean)

  return (
    <Card className="py-5">
      <CardContent className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-semibold">{scene.name}</p>
            {scene.shown ? <Badge>shown</Badge> : null}
          </div>
          <p className="mt-1 text-[13px] text-muted-foreground">{parts.join(' · ')}</p>
        </div>
        <Button disabled={scene.shown} onClick={onShow}>
          {scene.shown ? 'Shown' : 'Show'}
        </Button>
      </CardContent>
    </Card>
  )
}

function formatBytes(n?: number): string | null {
  if (n == null || n <= 0) return null
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`
}
