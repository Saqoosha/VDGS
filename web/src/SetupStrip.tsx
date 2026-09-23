import { Button } from '@/components/ui/button'
import { send } from './bridge'
import { Section } from './chrome'
import { Progress } from './components/Progress'
import { how, initialLang } from './i18n'
import type { SetupState } from './types'

/**
 * Whether the host's current job belongs to this section - finding the game, putting
 * the mod on, taking it off - as opposed to the track table's. The strings are the
 * host's own (`run_busy` in lib.rs); the table shows everything this does not claim.
 */
export function busyIsSetup(busy: string | null | undefined): boolean {
  return (
    busy === 'looking for velocidrone' ||
    busy === 'installing the mod' ||
    busy === 'removing the mod'
  )
}

/**
 * The first section under the masthead: point the app at the game, and get the mod onto it.
 *
 * This was a tab of its own. It holds the two things that only make sense before the
 * game exists at all - finding it, and putting the mod on it - which is a screen someone
 * visits once and then only when something is wrong. So it is a strip, not a page, and it
 * is not padded out: when everything is in order it is one line of path and three
 * buttons, and the verdict and the True Lens warning open under it only when there is
 * something to say.
 */
export function SetupStrip({ state }: { state: SetupState | null }) {
  // Nothing may be started while the game holds the files, or while the last job is
  // still copying.
  const busy = (state?.running ?? false) || !!state?.busy
  const game = state?.game ?? null
  const t = how[initialLang()]

  return (
    <Section label="setup" flush className="pb-2">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        {game ? (
          // select-text: this path is exactly what someone needs to paste into a support
          // message, or navigate to by hand, when something about the install is wrong -
          // the same reason the log stays selectable.
          <p className="min-w-0 flex-1 font-mono text-[12px] leading-relaxed break-all text-foreground/90 select-text">
            {game}
          </p>
        ) : (
          <p className="flex-1 font-serif text-xl font-light text-muted-foreground">
            not found on this machine
          </p>
        )}
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <Button variant="outline" size="sm" onClick={() => send('pick')}>
          Change…
        </Button>
        <Button
          variant="outline"
          size="sm"
          disabled={!game || busy || !state?.bundledMod}
          onClick={() => send('installMod')}
        >
          {modAction(state)}
        </Button>
        <Button
          variant="destructive"
          size="sm"
          disabled={!game || busy || !state?.mod}
          onClick={() => send('uninstallMod')}
        >
          Uninstall
        </Button>
        {/* Beside the buttons while there is room, under them when there is not: it is
            the result of pressing one. Kept quiet when the mod is simply installed - the
            masthead already says ready, and saying it twice is what made this a tab. */}
        {state && !state.busy ? <Verdict state={state} /> : null}
      </div>

      {/* Under the button that started it. Installing copies forty-odd files past a
          virus scanner; without this the window looks like the click did nothing. */}
      {state?.busy && busyIsSetup(state.busy) ? (
        <Progress what={state.busy} percent={state.busyPercent} />
      ) : null}

      {/* Not on the website: with True Lens on every capture is drawn and none of it
          reaches the screen, every log says success, and the sky is empty - a note
          elsewhere is useless because the finger is already here. null/false must not warn. */}
      {/* The mod travels inside this app, so an app nobody updates keeps installing the
          old mod - on 2026-09-23 the R6 sky needed a new companion and the old one could
          not say so. Shown only for a release strictly newer than this one. */}
      {state?.appUpdate ? (
        <div className="mt-4 border-l-2 border-primary bg-primary/10 px-4 py-3">
          <p className="font-mono text-[11px] tracking-[0.18em] text-primary uppercase">
            {t.setupAppUpdateHead} — {state.appUpdate}
          </p>
          <p className="mt-1.5 text-[14px] leading-relaxed text-foreground">
            {t.setupAppUpdateBody}
          </p>
          <Button className="mt-3" size="sm" onClick={() => send('openAppUpdate')}>
            {t.setupAppUpdateButton}
          </Button>
        </div>
      ) : null}

      {state?.trueLens === true ? (
        // Loud on purpose. At the size of the other notes it sat in a column of small
        // grey monospace and read as one more caption, which is the same as not being
        // there. The symptom leads, because that is what the reader is about to
        // experience; the setting's name follows as the thing to go and change.
        <div className="mt-4 border-l-2 border-destructive bg-destructive/10 px-4 py-3">
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
    </Section>
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

/**
 * What the strip says beside its buttons. Two states used to share one red line:
 * "nothing of ours is here" - the normal state before Install mod and right after
 * Uninstall - and "some of it is missing", which is an install that broke. Only the
 * second is a fault; the first just names the button to press.
 */
function Verdict({ state }: { state: SetupState }) {
  if (!state.game)
    return (
      <span className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
        point at the folder holding velocidrone
      </span>
    )
  if (!state.mod)
    return (
      <span className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
        not installed
      </span>
    )
  if (state.missing.length)
    return (
      <span className="font-mono text-[11px] tracking-[0.14em] text-destructive uppercase">
        missing: {state.missing.join(' · ')}
      </span>
    )
  return null
}
