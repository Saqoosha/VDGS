import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { send } from '../bridge'
import { how, initialLang } from '../i18n'
import type { SetupState } from '../types'

/**
 * Tab 01: point the app at the game, and get the mod onto it.
 *
 * Tracks and captures used to live here too, but that put the same list in three places
 * across the companion and the control UI. They moved to their own tab - this one is
 * left with the two things that only make sense before the game exists at all: finding
 * it, and putting the mod on it.
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
  const t = how[initialLang()]

  return (
    <>
      <Section n="01" label="velocidrone" flush>
        {game ? (
          // select-text: this path is exactly what someone needs to paste into a support
          // message, or navigate to by hand, when something about the install is wrong -
          // the same reason the log below stays selectable.
          <p className="font-mono text-[12px] leading-relaxed break-all text-foreground/90 select-text">
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

      {log.length ? (
        // select-text: this is what a person copies into a bug report when something
        // goes wrong - collect-mac-diagnostics.sh exists because getting logs out of
        // people matters. Set once here rather than per <li>: user-select inherits down.
        <ol className="mt-6 border-t border-rule pt-4 font-mono text-[11px] leading-relaxed text-muted-foreground select-text">
          {log.map((line, i) => (
            <li key={i} className="break-all">
              {line}
            </li>
          ))}
        </ol>
      ) : null}

      {/* Not on the website: with True Lens on every capture is drawn and none of it
          reaches the screen, every log says success, and the sky is empty - a note
          elsewhere is useless because the finger is already here. null/false must not warn. */}
      {state?.trueLens === true ? (
        // Loud on purpose. At the size of the other notes it sat in a column of small
        // grey monospace and read as one more caption, which is the same as not being
        // there. The symptom leads, because that is what the reader is about to
        // experience; the setting's name follows as the thing to go and change.
        <div className="mt-6 border-l-2 border-destructive bg-destructive/10 px-4 py-3">
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
