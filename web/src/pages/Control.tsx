import { useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { runExclusive } from '../busy'
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
import { formatBytes } from '../format'
import { fromSlider, fromYSlider, toSlider, toYSlider } from '../sliders'
import { useStatus } from '../useStatus'
import type { CollisionView, Scene, UpAxis } from '../types'

const UP_AXES: UpAxis[] = ['+x', '-x', '+y', '-y', '+z', '-z']

/**
 * The tuning section of tab 03 (③): what used to be the whole in-game control UI's §01
 * "current track" + §02 "on screen" + §03 "bindings", cut down to only what makes sense
 * once a track binds its capture automatically at creation time. There is nothing left
 * to bind or unbind by hand here, and no reason to hide the one capture a track just
 * pointed at - so this talks straight to the plugin's own HTTP server (useStatus/api.ts,
 * not the companion's host bridge) and shows whatever is on screen.
 */
export default function Control() {
  const { state, refresh } = useStatus()
  const [flash, setFlash] = useState('')
  const dragging = useRef(false)

  const showFlash = (msg: string) => {
    setFlash(msg)
    window.setTimeout(() => setFlash(''), 2000)
  }

  const act = async (fn: () => Promise<void>) => {
    try {
      await runExclusive(async () => {
        await fn()
        await refresh()
      })
    } catch (e) {
      showFlash(e instanceof Error ? e.message : 'failed')
      await refresh()
    }
  }

  const available = state?.available ?? []
  const loaded = state?.loaded ?? []
  const shownName = loaded[0]
  const shown = available.find((s) => s.name === shownName)

  return (
    <div>
      {available.length ? (
        <ol className="mb-6">
          {available.map((s, i) => (
            <CaptureRow
              key={s.name}
              index={String(i + 1).padStart(2, '0')}
              scene={s}
              onShow={() => void act(() => api.load(s.name))}
            />
          ))}
        </ol>
      ) : (
        <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
          nothing installed yet
        </p>
      )}

      {flash ? (
        <p className="mb-4 font-mono text-[11px] tracking-[0.14em] text-live uppercase" role="status">
          {flash}
        </p>
      ) : null}

      {shown ? (
        <ShownBlock scene={shown} dragging={dragging} onRefresh={refresh} onFlash={showFlash} />
      ) : null}
    </div>
  )
}

function CaptureRow({
  index,
  scene,
  onShow,
}: {
  index: string
  scene: Scene
  onShow: () => void
}) {
  return (
    <li className="grid grid-cols-[2.25rem_minmax(0,1fr)_auto] items-start gap-3 border-b border-rule/80 py-4 last:border-b-0">
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
          {formatBytes(scene.bytes) ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {formatBytes(scene.bytes)}
            </>
          ) : null}
        </p>
      </div>
      <Button variant={scene.shown ? 'ghost' : 'default'} disabled={scene.shown} onClick={onShow}>
        {scene.shown ? 'Shown' : 'Show'}
      </Button>
    </li>
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
  const [x, setX] = useStateSafe(scene.x, dragging)
  const [z, setZ] = useStateSafe(scene.z, dragging)
  const [turn, setTurn] = useStateSafe(scene.turn, dragging)

  const pushTransform = async (v: { scale?: number; y?: number; x?: number; z?: number }) => {
    try {
      await api.setTransform(scene.name, v)
    } catch (e) {
      onFlash(e instanceof Error ? e.message : 'failed')
    }
  }

  // No onRefresh here, matching pushTransform below: turn is a dragged slider, and
  // refetching status after every drag tick would be a poll storm. The two callers that
  // do need a refresh - up (can detach the backdrop server-side) and mirror (its checked
  // state must come from the server, not an optimistic echo) - fetch it themselves.
  const pushOrientation = async (v: { up?: string; turn?: number; mirror?: boolean }) => {
    try {
      await api.setOrientation(scene.name, v)
    } catch (e) {
      onFlash(e instanceof Error ? e.message : 'failed')
    }
  }

  // The floor clamp SplatBackdrop attaches is measured in the parent's local space on
  // the assumption that local -Y is world down - true only while up is +y. The plugin
  // already refuses to attach the box on anything else and just logs why (WebControl
  // always answers 200), so a checkbox left enabled here would look broken: checked,
  // then silently unchecking itself on the next poll with no visible cause. `up: null`
  // is left enabled on purpose - it can mean "never touched" (upright) just as easily as
  // "a hand-written placement.json with a raw rotation" (see the Up control below), and
  // disabling on a guess would be its own kind of dead button.
  const rotated = scene.up != null && scene.up !== '+y'

  return (
    <div>
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="font-serif text-3xl leading-[1.05] font-light">{scene.name}</h2>
          <p className="mt-2 font-mono text-sm tabular-nums text-muted-foreground">
            {scene.splats ? scene.splats.toLocaleString() : '—'} splats
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-5 text-sm">
          <div>
            <StampCheck
              label="box"
              checked={scene.backdrop}
              disabled={rotated}
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
            {rotated ? (
              <p className="mt-1 font-mono text-[10px] tracking-[0.1em] text-muted-foreground">
                rotated — box would slice it
              </p>
            ) : null}
          </div>
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

      <div className="mt-6">
        <div className="mb-1 font-mono text-[11px] tracking-[0.18em] text-muted-foreground uppercase">
          up
        </div>
        <div className="flex flex-wrap gap-2">
          {UP_AXES.map((axis) => (
            <Button
              key={axis}
              size="sm"
              variant={scene.up === axis ? 'default' : 'outline'}
              className="font-mono uppercase"
              onClick={() => {
                void (async () => {
                  await pushOrientation({ up: axis })
                  await onRefresh()
                })()
              }}
            >
              {axis}
            </Button>
          ))}
        </div>
        {scene.up == null ? (
          // The honest answer for a control that cannot show an arbitrary euler triple:
          // a placement.json with a raw `rotation` array and no `up` key reports exactly
          // this, and it can be genuinely rotated even though nothing here looks touched.
          // Saying "no orientation set" would be a guess this cannot back up either way.
          <p className="mt-1.5 font-mono text-[10px] tracking-[0.1em] text-muted-foreground">
            up not recorded — may already be upright, or hold a rotation these buttons
            can't show; picking one replaces it
          </p>
        ) : null}
      </div>

      <div className="mt-5 flex flex-wrap items-center gap-5">
        {/* Only a .ply is mirrored at load time - SplatData.Load (a converted directory)
            reads packed buffers and cannot flip them without decoding every format. The
            plugin answers 200 and logs the request as ignored, so offering this on a
            converted capture would be a button that always looks like it did nothing. */}
        {scene.kind === 'ply' ? (
          <StampCheck
            label="mirror"
            checked={scene.mirror}
            onChange={(on) => {
              void (async () => {
                await pushOrientation({ mirror: on })
                await onRefresh()
              })()
            }}
          />
        ) : null}
      </div>

      <div className="mt-6 flex flex-col gap-6">
        <Dial
          label="Turn"
          valueLabel={turn.toFixed(0) + '°'}
          min={0}
          max={360}
          step={1}
          slider={turn}
          numberValue={turn.toFixed(0)}
          numberStep={1}
          numberMin={0}
          numberMax={360}
          onPointer={() => {
            dragging.current = true
          }}
          onPointerUp={() => {
            dragging.current = false
          }}
          onSlide={(t) => {
            setTurn(t)
            void pushOrientation({ turn: t })
          }}
          onNumber={(v) => {
            setTurn(v)
            void pushOrientation({ turn: v })
          }}
        />
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
            void pushTransform({ scale: v })
          }}
          onNumber={(v) => {
            setScale(v)
            void pushTransform({ scale: v })
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
            void pushTransform({ y: v })
          }}
          onNumber={(v) => {
            setY(v)
            void pushTransform({ y: v })
          }}
        />
        <Dial
          label="X"
          valueLabel={x.toFixed(2) + 'm'}
          min={-1}
          max={1}
          step={0.001}
          slider={toYSlider(x)}
          numberValue={x.toFixed(2)}
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
            setX(v)
            void pushTransform({ x: v })
          }}
          onNumber={(v) => {
            setX(v)
            void pushTransform({ x: v })
          }}
        />
        <Dial
          label="Z"
          valueLabel={z.toFixed(2) + 'm'}
          min={-1}
          max={1}
          step={0.001}
          slider={toYSlider(z)}
          numberValue={z.toFixed(2)}
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
            setZ(v)
            void pushTransform({ z: v })
          }}
          onNumber={(v) => {
            setZ(v)
            void pushTransform({ z: v })
          }}
        />
      </div>
    </div>
  )
}

function StampCheck({
  label,
  checked,
  disabled,
  onChange,
}: {
  label: string
  checked: boolean
  disabled?: boolean
  onChange: (on: boolean) => void
}) {
  return (
    <label className="flex items-center gap-2 font-mono text-[11px] tracking-[0.16em] uppercase">
      <Checkbox checked={checked} disabled={disabled} onCheckedChange={(v) => onChange(v === true)} />
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
