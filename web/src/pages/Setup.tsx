import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { formatBytes } from '../format'
import { send } from '../bridge'
import { how, initialLang } from '../i18n'
import type { SetupState, TrackEntry } from '../types'

/**
 * The companion app's one page: point it at the game, put tracks in, fly.
 *
 * The list is of tracks rather than captures because that is the unit the player acts in
 * - they pick a track in VelociDrone and the capture bound to its name appears. A capture
 * nothing points at shows nothing, so it is reported apart from the list rather than in it.
 */
export default function Setup({
  state,
  log,
}: {
  state: SetupState | null
  log: string[]
}) {
  // Nothing may be started while the game holds the files, or while the last job is
  // still copying.
  const busy = (state?.running ?? false) || !!state?.busy
  const game = state?.game ?? null
  const tracks = state?.tracks ?? []
  const unbound = state?.unbound ?? []
  const t = how[initialLang()]

  return (
    <>
      <Section n="01" label="velocidrone" flush>
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
          <Button
            variant="outline"
            disabled={!game || busy || !state?.bundledMod}
            onClick={() => send('installMod')}
          >
            {modAction(state)}
          </Button>
          <Button
            variant="destructive"
            disabled={!game || busy || !state?.mod}
            onClick={() => send('uninstallMod')}
          >
            Uninstall
          </Button>
        </div>
        {/* Under the buttons, not beside them: it is the result of pressing one, and
            three buttons and a sentence do not share a line on a narrow window. */}
        {state ? (
          <div className="mt-3">
            {state.busy ? (
              <span className="font-mono text-[11px] tracking-[0.14em] text-signal uppercase">
                <span className="mr-2 animate-pulse">◐</span>
                {state.busy}
                {state.busyPercent != null ? ` ${state.busyPercent}%` : '…'}
              </span>
            ) : (
              <Verdict state={state} />
            )}
          </div>
        ) : null}
      </Section>

      <Section n="02" label="tracks" className="flex min-h-0 flex-1 flex-col">
        <div className="min-h-0 flex-1 overflow-y-auto pr-1">
          {tracks.length ? (
            <ol>
              {tracks.map((t, i) => (
                <TrackRow
                  key={t.track}
                  index={String(i + 1).padStart(2, '0')}
                  entry={t}
                  disabled={busy}
                />
              ))}
            </ol>
          ) : (
            <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
              {game ? 'no vdgs tracks yet' : 'no game folder'}
            </p>
          )}

          {unbound.length ? (
            <p className="mt-5 font-mono text-[11px] leading-relaxed text-muted-foreground">
              installed, on no track: {unbound.map((c) => c.name).join(' · ')}
            </p>
          ) : null}

          {log.length ? (
            <ol className="mt-6 border-t border-rule pt-3 font-mono text-[11px] leading-relaxed text-muted-foreground">
              {log.map((line, i) => (
                <li key={i} className="break-all">
                  {line}
                </li>
              ))}
            </ol>
          ) : null}
        </div>

        <div className="mt-4 flex flex-wrap gap-3">
          <Button variant="outline" disabled={!game || busy} onClick={() => send('addTrack')}>
            Add track
          </Button>
          <Button variant="outline" disabled={!game || busy} onClick={() => send('installCapture')}>
            Install capture
          </Button>
          {state?.running ? (
            <span className="self-center font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
              close velocidrone first
            </span>
          ) : null}
        </div>
      </Section>

      {/* Above Fly, not on the website: with True Lens on every capture is drawn and none
          of it reaches the screen, every log says success, and the sky is empty — a note
          elsewhere is useless because the finger is already here. null/false must not warn. */}
      <div className="mt-6 shrink-0">
        {state?.trueLens === true ? (
          // Loud on purpose. At the size of the other notes it sat in a column of small
          // grey monospace and read as one more caption, which is the same as not being
          // there. The symptom leads, because that is what the reader is about to
          // experience; the setting's name follows as the thing to go and change.
          <div className="mb-4 border-l-2 border-destructive bg-destructive/10 px-4 py-3">
            <p className="font-mono text-[11px] tracking-[0.18em] text-destructive uppercase">
              {t.setupTrueLensHead}
            </p>
            <p className="mt-1.5 text-[14px] leading-relaxed text-foreground">
              {t.setupTrueLensA}
              <b lang="en" className="text-destructive">
                True Lens
              </b>
              {t.setupTrueLensB}
            </p>
          </div>
        ) : null}
        <Button
          size="lg"
          disabled={!game || busy}
          onClick={() => send('fly')}
          className="h-14 w-full font-mono text-base tracking-[0.3em] uppercase"
        >
          Fly
        </Button>
      </div>
    </>
  )
}

/**
 * The mod travels inside this app, so the button installs what it carries rather than
 * asking for a file. Saying which of the three things it will do keeps someone from
 * reinstalling over a working setup to find out.
 */
function modAction(state: SetupState | null): string {
  if (!state?.bundledMod) return 'No mod payload'
  if (!state.mod) return 'Install mod'
  return state.mod === state.bundledMod ? 'Reinstall mod' : `Update to ${state.bundledMod}`
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

function TrackRow({
  index,
  entry,
  disabled,
}: {
  index: string
  entry: TrackEntry
  disabled: boolean
}) {
  return (
    <li className="group/row grid grid-cols-[2.25rem_minmax(0,1fr)_auto] items-start gap-3 border-b border-rule/80 py-4 last:border-b-0">
      <span className="pt-1 font-mono text-[11px] text-muted-foreground">{index}</span>
      <div className="min-w-0">
        <div className="flex flex-wrap items-baseline gap-3">
          <p className="font-serif text-[1.65rem] leading-tight font-light">{entry.track}</p>
          {!entry.inGame ? (
            // A binding whose track is not in the database shows nothing and says nothing,
            // in the game or here, unless it is called out.
            <span className="font-mono text-[10px] tracking-[0.2em] text-muted-foreground uppercase">
              not in velocidrone
            </span>
          ) : null}
        </div>
        {entry.captureInstalled ? (
          <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
            {entry.capture}
            <span className="mx-2 text-rule">/</span>
            {entry.splats ? entry.splats.toLocaleString() : '—'} splats
            <span className="mx-2 text-rule">/</span>
            {/* A .ply is read and converted every time the capture is shown, which is
                seconds of stutter a converted directory does not cost. */}
            {entry.converted ? 'converted' : 'ply'}
            {formatBytes(entry.bytes) ? (
              <>
                <span className="mx-2 text-rule">/</span>
                {formatBytes(entry.bytes)}
              </>
            ) : null}
            <span className="mx-2 text-rule">/</span>
            {/* Without a mesh the capture is flown straight through, and nothing in the
                game says so - which is why it is stated either way. */}
            {entry.collision ? 'collision' : 'no collision'}
          </p>
        ) : (
          <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-destructive">
            {entry.capture ?? 'nothing'} is not installed
          </p>
        )}
      </div>
      {/* Kept quiet until the row is pointed at: this is a list to read, and the button
          is for the one row in it someone wants gone. */}
      <Button
        variant="ghost"
        size="sm"
        disabled={disabled}
        onClick={() => send('removeTrack', entry.track)}
        className="opacity-0 transition-opacity group-hover/row:opacity-100 focus-visible:opacity-100"
        aria-label={
          entry.fromServer ? `Unbind ${entry.track}` : `Remove ${entry.track}`
        }
      >
        {entry.fromServer ? 'Unbind' : 'Remove'}
      </Button>
    </li>
  )
}
