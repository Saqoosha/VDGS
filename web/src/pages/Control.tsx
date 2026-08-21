import { useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { runExclusive } from '../busy'
import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
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
  const keys = Object.keys(bindings)

  return (
    <div>
      <Section n="01" label="current track">
        <h2
          className={
            'font-serif text-[2.6rem] leading-[0.95] font-light tracking-tight md:text-5xl ' +
            (track ? '' : 'text-muted-foreground italic')
          }
        >
          {track ?? 'no track loaded'}
        </h2>
        {track ? (
          <p className="mt-3 font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            {bound && bound.length ? (
              <>
                bound → <span className="text-foreground">{bound.join(', ')}</span>
              </>
            ) : (
              'not bound to any splat'
            )}
          </p>
        ) : null}
        <div className="mt-6 flex flex-wrap items-center gap-2">
          <Button disabled={!track} onClick={() => void act(() => api.bind(loaded), 'saved')}>
            Bind shown
          </Button>
          <Button
            variant="outline"
            disabled={!track || !bound}
            onClick={() => void act(() => api.unbind(), 'unbound')}
          >
            Unbind
          </Button>
          <Button variant="outline" onClick={() => void act(() => api.unload())}>
            Hide all
          </Button>
          {flash ? (
            <p className="font-mono text-[11px] tracking-[0.14em] text-live uppercase" role="status">
              {flash}
            </p>
          ) : null}
        </div>
      </Section>

      {shown ? (
        <ShownBlock
          scene={shown}
          dragging={dragging}
          onRefresh={refresh}
          onFlash={showFlash}
        />
      ) : (
        <Section n="02" label="on screen">
          <p className="font-serif text-2xl font-light text-muted-foreground italic">
            nothing shown
          </p>
        </Section>
      )}

      <Section n="03" label="bindings">
        {keys.length === 0 ? (
          <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            none
          </p>
        ) : (
          <ul>
            {keys.map((k) => (
              <li
                key={k}
                className="flex items-baseline justify-between gap-4 border-b border-rule/70 py-3 last:border-b-0"
              >
                <div className="min-w-0">
                  <p className="font-serif text-xl leading-tight">{k}</p>
                  <p className="mt-1 font-mono text-[11px] tracking-[0.08em] text-muted-foreground">
                    {bindings[k].join(', ')}
                  </p>
                </div>
                <Button
                  variant="ghost"
                  size="sm"
                  className="text-muted-foreground"
                  onClick={() => void act(() => api.unbind(k), 'removed')}
                >
                  remove
                </Button>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </div>
  )
}

function ShownBlock({
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
    <Section n="02" label="on screen">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="font-serif text-3xl leading-[1.05] font-light">{scene.name}</h2>
          <p className="mt-2 font-mono text-sm tabular-nums text-muted-foreground">
            {scene.splats ? scene.splats.toLocaleString() : '—'} splats
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-5 text-sm">
          <StampCheck
            label="box"
            checked={scene.backdrop}
            onChange={(on) => {
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
          {scene.hasCollision ? (
            <>
              <StampCheck
                label="solid"
                checked={scene.collision}
                onChange={(on) => {
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
                <SelectTrigger className="w-[148px] font-mono text-[11px] tracking-[0.12em] uppercase" size="sm">
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
      </div>

      <div className="mt-8 flex flex-col gap-6">
        <Dial
          label="Scale"
          valueLabel={scale.toFixed(2) + '×'}
          min={-2}
          max={2}
          step={0.002}
          slider={toSlider(scale)}
          numberValue={scale.toFixed(3)}
          numberStep={0.01}
          numberMin={0.01}
          numberMax={100}
          onPointer={() => {
            dragging.current = true
          }}
          onPointerUp={() => {
            dragging.current = false
          }}
          onSlide={(t) => {
            const v = fromSlider(t)
            setScale(v)
            void push(v, undefined)
          }}
          onNumber={(v) => {
            setScale(v)
            void push(v, undefined)
          }}
        />
        <Dial
          label="Height"
          valueLabel={y.toFixed(2) + 'm'}
          min={-1}
          max={1}
          step={0.001}
          slider={toYSlider(y)}
          numberValue={y.toFixed(2)}
          numberStep={0.05}
          numberMin={-1000}
          numberMax={1000}
          onPointer={() => {
            dragging.current = true
          }}
          onPointerUp={() => {
            dragging.current = false
          }}
          onSlide={(t) => {
            const v = fromYSlider(t)
            setY(v)
            void push(undefined, v)
          }}
          onNumber={(v) => {
            setY(v)
            void push(undefined, v)
          }}
        />
      </div>
    </Section>
  )
}

function StampCheck({
  label,
  checked,
  onChange,
}: {
  label: string
  checked: boolean
  onChange: (on: boolean) => void
}) {
  return (
    <label className="flex items-center gap-2 font-mono text-[11px] tracking-[0.16em] uppercase">
      <Checkbox checked={checked} onCheckedChange={(v) => onChange(v === true)} />
      {label}
    </label>
  )
}

function Dial({
  label,
  valueLabel,
  min,
  max,
  step,
  slider,
  numberValue,
  numberStep,
  numberMin,
  numberMax,
  onPointer,
  onPointerUp,
  onSlide,
  onNumber,
}: {
  label: string
  valueLabel: string
  min: number
  max: number
  step: number
  slider: number
  numberValue: string
  numberStep: number
  numberMin: number
  numberMax: number
  onPointer: () => void
  onPointerUp: () => void
  onSlide: (t: number) => void
  onNumber: (v: number) => void
}) {
  return (
    <div className="flex flex-wrap items-end gap-4">
      <div className="w-16 font-mono text-[11px] tracking-[0.18em] text-muted-foreground uppercase">
        {label}
      </div>
      <input
        type="range"
        className="dial min-w-[160px] flex-1"
        min={min}
        max={max}
        step={step}
        value={slider}
        onPointerDown={onPointer}
        onPointerUp={onPointerUp}
        onChange={(e) => onSlide(parseFloat(e.target.value))}
      />
      <span className="w-[4.5rem] text-right font-mono text-lg tabular-nums">
        {valueLabel}
      </span>
      <Input
        type="number"
        className="h-8 w-[92px] bg-transparent font-mono"
        step={numberStep}
        min={numberMin}
        max={numberMax}
        value={numberValue}
        onChange={(e) => {
          const v = parseFloat(e.target.value)
          if (!Number.isFinite(v)) return
          onNumber(v)
        }}
      />
    </div>
  )
}

function useStateSafe(value: number, dragging: { current: boolean }) {
  const [local, setLocal] = useState(value)
  useEffect(() => {
    if (!dragging.current) setLocal(value)
  }, [value, dragging])
  return [local, setLocal] as const
}
