import { useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { runExclusive } from '../busy'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableRow,
} from '@/components/ui/table'
import { fromSlider, fromYSlider, toSlider, toYSlider } from '../sliders'
import { useStatusContext } from '../status-context'
import type { CollisionView, Scene } from '../types'

export default function Control() {
  const { state, refresh } = useStatusContext()
  const [flash, setFlash] = useState('')
  const dragging = useRef(false)

  const showFlash = (msg: string) => {
    setFlash(msg)
    window.setTimeout(() => setFlash(''), 2000)
  }

  const act = async (fn: () => Promise<void>, ok?: string) => {
    try {
      await runExclusive(async () => {
        await fn()
        await refresh()
      })
      if (ok) showFlash(ok)
    } catch (e) {
      showFlash(e instanceof Error ? e.message : 'failed')
      await refresh()
    }
  }

  const track = state?.track ?? null
  const bindings = state?.bindings ?? {}
  const loaded = state?.loaded ?? []
  const available = state?.available ?? []
  const bound = track ? bindings[track] : undefined
  const shownName = loaded[0]
  const shown = available.find((s) => s.name === shownName)

  return (
    <div className="flex flex-col gap-5">
      <Card className="py-6">
        <CardHeader>
          <p className="text-[11px] font-medium tracking-[0.08em] text-muted-foreground uppercase">
            Current track
          </p>
          <CardTitle className={track ? 'text-lg' : 'text-lg font-normal text-muted-foreground'}>
            {track ?? 'no track loaded'}
          </CardTitle>
          {track ? (
            <p className="text-sm text-muted-foreground">
              {bound && bound.length ? (
                <>
                  bound to <span className="font-medium text-foreground">{bound.join(', ')}</span>
                </>
              ) : (
                'not bound to any splat'
              )}
            </p>
          ) : null}
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          <Button
            disabled={!track}
            onClick={() => void act(() => api.bind(loaded), 'saved')}
          >
            Bind shown splat to this track
          </Button>
          <Button
            variant="outline"
            disabled={!track || !bound}
            onClick={() => void act(() => api.unbind(), 'unbound')}
          >
            Unbind this track
          </Button>
          <Button variant="outline" onClick={() => void act(() => api.unload())}>
            Hide all
          </Button>
          {flash ? (
            <p className="self-center text-sm text-emerald-700" role="status">
              {flash}
            </p>
          ) : null}
        </CardContent>
      </Card>

      {shown ? (
        <ShownCard
          scene={shown}
          dragging={dragging}
          onRefresh={refresh}
          onFlash={showFlash}
        />
      ) : null}

      <Card className="py-6">
        <CardHeader>
          <p className="text-[11px] font-medium tracking-[0.08em] text-muted-foreground uppercase">
            Bindings
          </p>
        </CardHeader>
        <CardContent>
          {Object.keys(bindings).length === 0 ? (
            <p className="text-muted-foreground">none</p>
          ) : (
            <Table>
              <TableBody>
                {Object.keys(bindings).map((k) => (
                  <TableRow key={k}>
                    <TableCell className="font-medium">{k}</TableCell>
                    <TableCell>{bindings[k].join(', ')}</TableCell>
                    <TableCell className="w-[1%] text-right">
                      <Button
                        variant="outline"
                        size="sm"
                        onClick={() => void act(() => api.unbind(k), 'removed')}
                      >
                        remove
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

function ShownCard({
  scene,
  dragging,
  onRefresh,
  onFlash,
}: {
  scene: Scene
  dragging: { current: boolean }
  onRefresh: () => Promise<void>
  onFlash: (msg: string) => void
}) {
  const [scale, setScale] = useStateSafe(scene.scale, dragging)
  const [y, setY] = useStateSafe(scene.y, dragging)

  const push = async (nextScale?: number, nextY?: number) => {
    try {
      await api.setTransform(scene.name, nextScale, nextY)
    } catch (e) {
      onFlash(e instanceof Error ? e.message : 'failed')
    }
  }

  return (
    <Card className="py-6">
      <CardHeader>
        <p className="text-[11px] font-medium tracking-[0.08em] text-muted-foreground uppercase">
          Shown splat
        </p>
        <CardTitle className="text-lg">{scene.name}</CardTitle>
        <p className="text-sm text-muted-foreground">
          {scene.splats ? scene.splats.toLocaleString() + ' splats' : ''}
        </p>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-4 text-sm">
          <label className="flex items-center gap-2">
            <Checkbox
              checked={scene.backdrop}
              onCheckedChange={(v) => {
                const on = v === true
                void (async () => {
                  try {
                    await api.setBackdrop(scene.name, on)
                    await onRefresh()
                  } catch (e) {
                    onFlash(e instanceof Error ? e.message : 'failed')
                    await onRefresh()
                  }
                })()
              }}
            />
            box
          </label>
          {scene.hasCollision ? (
            <>
              <label className="flex items-center gap-2">
                <Checkbox
                  checked={scene.collision}
                  onCheckedChange={(v) => {
                    const on = v === true
                    void (async () => {
                      try {
                        await api.setCollision(scene.name, on)
                        await onRefresh()
                      } catch (e) {
                        onFlash(e instanceof Error ? e.message : 'failed')
                        await onRefresh()
                      }
                    })()
                  }}
                />
                solid
              </label>
              <Select
                value={scene.collisionView || 'off'}
                onValueChange={(mode) => {
                  void (async () => {
                    try {
                      await api.setCollisionView(scene.name, mode as CollisionView)
                      await onRefresh()
                    } catch (e) {
                      onFlash(e instanceof Error ? e.message : 'failed')
                      await onRefresh()
                    }
                  })()
                }}
              >
                <SelectTrigger className="w-[140px]" size="sm">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="off">hide mesh</SelectItem>
                  <SelectItem value="solid">show solid</SelectItem>
                  <SelectItem value="wire">show wire</SelectItem>
                </SelectContent>
              </Select>
            </>
          ) : null}
        </div>

        <div className="flex flex-col gap-3 rounded-lg bg-muted/50 p-4 ring-1 ring-foreground/10">
          <div className="flex flex-wrap items-center gap-3">
            <Label className="w-16 text-[11px] tracking-[0.08em] text-muted-foreground uppercase">
              Scale
            </Label>
            <input
              type="range"
              min={-2}
              max={2}
              step={0.002}
              className="min-w-[130px] flex-1"
              value={toSlider(scale)}
              onPointerDown={() => {
                dragging.current = true
              }}
              onPointerUp={() => {
                dragging.current = false
              }}
              onChange={(e) => {
                const v = fromSlider(parseFloat(e.target.value))
                setScale(v)
                void push(v, undefined)
              }}
            />
            <span className="w-14 text-right font-medium tabular-nums">
              {scale.toFixed(2)}×
            </span>
            <Input
              type="number"
              className="w-[88px]"
              step={0.01}
              min={0.01}
              max={100}
              value={scale.toFixed(3)}
              onChange={(e) => {
                const v = parseFloat(e.target.value)
                if (!Number.isFinite(v)) return
                setScale(v)
                void push(v, undefined)
              }}
            />
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <Label className="w-16 text-[11px] tracking-[0.08em] text-muted-foreground uppercase">
              Height
            </Label>
            <input
              type="range"
              min={-1}
              max={1}
              step={0.001}
              className="min-w-[130px] flex-1"
              value={toYSlider(y)}
              onPointerDown={() => {
                dragging.current = true
              }}
              onPointerUp={() => {
                dragging.current = false
              }}
              onChange={(e) => {
                const v = fromYSlider(parseFloat(e.target.value))
                setY(v)
                void push(undefined, v)
              }}
            />
            <span className="w-14 text-right font-medium tabular-nums">
              {y.toFixed(2)}m
            </span>
            <Input
              type="number"
              className="w-[88px]"
              step={0.05}
              min={-1000}
              max={1000}
              value={y.toFixed(2)}
              onChange={(e) => {
                const v = parseFloat(e.target.value)
                if (!Number.isFinite(v)) return
                setY(v)
                void push(undefined, v)
              }}
            />
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

function useStateSafe(value: number, dragging: { current: boolean }) {
  const [local, setLocal] = useState(value)
  useEffect(() => {
    if (!dragging.current) setLocal(value)
  }, [value, dragging])
  return [local, setLocal] as const
}
