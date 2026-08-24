import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { formatBytes } from '../format'
import { send } from '../bridge'
import type { Capture, SetupState } from '../types'

/**
 * The companion app's one page: what is installed, what is missing, and the button that
 * starts the game.
 *
 * Reading order is the order of the work - point at the game, put captures in, fly - so
 * Fly sits at the end rather than competing with the setup it depends on.
 */
export default function Setup({
  state,
  log,
}: {
  state: SetupState | null
  log: string[]
}) {
  const busy = state?.running ?? false
  const game = state?.game ?? null

  return (
    <div>
      <Section n="01" label="velocidrone">
        {game ? (
          <p className="font-mono text-[12px] leading-relaxed break-all text-foreground/90">
            {game}
          </p>
        ) : (
          <p className="font-serif text-xl font-light text-muted-foreground">
            not found on this machine
          </p>
        )}

        <div className="mt-3 flex flex-wrap items-center gap-3">
          <Button variant="outline" onClick={() => send('pick')}>
            Change…
          </Button>
          {state ? <Verdict state={state} /> : null}
        </div>
      </Section>

      <Section n="02" label="captures">
        {!state?.captures.length ? (
          <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            {game ? 'nothing installed yet' : 'no game folder'}
          </p>
        ) : (
          // A machine with a dozen captures would otherwise push Fly - the one action this
          // window exists for - two screens down.
          <ol className="max-h-[16rem] overflow-y-auto pr-1">
            {state.captures.map((c, i) => (
              <CaptureRow
                key={c.name}
                index={String(i + 1).padStart(2, '0')}
                capture={c}
              />
            ))}
          </ol>
        )}

        <div className="mt-5 flex flex-wrap gap-3">
          <Button variant="outline" disabled={!game || busy} onClick={() => send('installMod')}>
            Install mod
          </Button>
          <Button variant="outline" disabled={!game || busy} onClick={() => send('installCapture')}>
            Install capture
          </Button>
          <Button variant="outline" disabled={!game || busy} onClick={() => send('addTrack')}>
            Add track
          </Button>
        </div>
        {busy ? (
          <p className="mt-3 font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            velocidrone is running — close it before installing anything
          </p>
        ) : null}
      </Section>

      <Section n="03" label="fly">
        <div className="flex flex-wrap items-center justify-between gap-4">
          <p className="max-w-[26rem] font-mono text-[11px] leading-relaxed text-muted-foreground">
            {state?.running
              ? 'velocidrone is already running.'
              : state?.ready
                ? `starts the game with ${state.launchArgs}, which the captures need in order to draw at all.`
                : 'install the mod first.'}
          </p>
          <Button
            size="lg"
            disabled={!game || busy}
            onClick={() => send('fly')}
            className="min-w-[9rem] font-mono tracking-[0.2em] uppercase"
          >
            Fly
          </Button>
        </div>
      </Section>

      {log.length ? (
        <Section n="04" label="log">
          <ol className="font-mono text-[11px] leading-relaxed text-muted-foreground">
            {log.map((line, i) => (
              <li key={i} className="break-all">
                {line}
              </li>
            ))}
          </ol>
        </Section>
      ) : null}
    </div>
  )
}

function Verdict({ state }: { state: SetupState }) {
  if (!state.game)
    return (
      <span className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
        point at the folder holding velocidrone.exe
      </span>
    )
  if (state.missing.length)
    return (
      <span className="font-mono text-[11px] tracking-[0.14em] text-destructive uppercase">
        missing: {state.missing.join(' · ')}
      </span>
    )
  return (
    <span className="font-mono text-[11px] tracking-[0.14em] text-live uppercase">
      mod {state.mod} installed
    </span>
  )
}

function CaptureRow({ index, capture }: { index: string; capture: Capture }) {
  return (
    <li className="grid grid-cols-[2.25rem_minmax(0,1fr)] items-start gap-3 border-b border-rule/80 py-4 last:border-b-0">
      <span className="pt-1 font-mono text-[11px] text-muted-foreground">{index}</span>
      <div className="min-w-0">
        <p className="font-serif text-[1.65rem] leading-tight font-light">{capture.name}</p>
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
          {capture.splats ? capture.splats.toLocaleString() : '—'} splats
          {formatBytes(capture.bytes) ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {formatBytes(capture.bytes)}
            </>
          ) : null}
          <span className="mx-2 text-rule">/</span>
          {/* Without a mesh the capture is flown straight through, and nothing in the
              game says so - which is why it is stated here rather than only when absent. */}
          {capture.collision ? 'collision' : 'no collision'}
        </p>
      </div>
    </li>
  )
}
