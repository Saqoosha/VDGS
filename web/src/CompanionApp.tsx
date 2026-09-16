import { useEffect, useState } from 'react'
import { Masthead } from './chrome'
import { ParticleField } from './ParticleField'
import { Button } from '@/components/ui/button'
import { hosted, send, subscribe } from './bridge'
import { SetupStrip } from './SetupStrip'
import Tracks, { TracksToolbar, type Picked } from './pages/Tracks'
import Tweak from './pages/Tweak'
import type { SetupState } from './types'

/**
 * The companion app's window: one page. Same shell as the control UI, no router and no
 * tabs - a window with a strip for the game, a table of tracks, and Fly.
 *
 * It had three tabs. Tested end to end with a real capture, two of them turned out to
 * be the same thing - a track - split across two screens, with the seams showing: the
 * button that took a file was on one tab and the file it wanted was on the other, a
 * success on 03 rendered as "nothing installed" because the list it drew from emptied
 * on success, and the log lived on 01 while the buttons that fail lived on 02 and 03.
 * A player counts tracks. Everything now happens on the row of the track it concerns.
 *
 * Tuning is the one thing that gets its own screen, because it is a different mode
 * (the game is running and the capture is on screen) with a page of sliders; it takes
 * the table's place and comes back with ← tracks.
 */
export default function CompanionApp() {
  const [state, setState] = useState<SetupState | null>(null)
  const [log, setLog] = useState<string[]>([])
  const [showLog, setShowLog] = useState(false)
  const [picked, setPicked] = useState<Picked | null>(null)
  const [tweak, setTweak] = useState<string | null>(null)
  const [q, setQ] = useState('')

  useEffect(() => {
    const stop = subscribe((m) => {
      if (m.type === 'state') {
        const { type: _type, ...rest } = m
        setState(rest)
      } else if (m.type === 'progress') {
        setState((prev) => (prev ? { ...prev, busyPercent: m.percent } : prev))
      } else if (m.type === 'busy') {
        setState((prev) => (prev ? { ...prev, busy: m.what, busyPercent: null } : prev))
      } else if (m.type === 'running') {
        setState((prev) => (prev ? { ...prev, running: m.running } : prev))
      } else if (m.type === 'picked') {
        setPicked({ path: m.path, stem: m.stem })
      } else {
        // Long enough to see what happened, short enough that a session left open does
        // not grow without bound.
        setLog((prev) => [...prev, m.line].slice(-200))
      }
    })
    send('refresh')
    return stop
  }, [])

  // Folded together for anything that has to wait for either condition (Fly: can't fly
  // twice, and can't fly mid-job). Tracks needs the two apart - its Unbind only writes
  // bindings.json, a file the game never holds open, so it must stay live while the
  // game runs; only its file-touching siblings (Add track, Get, Remove) need the game
  // closed.
  const opBusy = !!state?.busy
  const running = state?.running ?? false
  const busy = running || opBusy
  const game = state?.game ?? null
  const latest = log.length ? log[log.length - 1] : null

  // Without a host there is no folder picker, no downloader and no track database: this
  // is the page the plugin serves at :8777, and tuning is all it can do.
  if (!hosted) {
    return (
      <div data-testid="companion-shell" className="text-foreground select-none">
        <ParticleField />
        <div className="relative mx-auto flex h-svh w-full max-w-[44rem] flex-col px-6 md:px-8">
          <div className="shrink-0 pt-7 [&>header]:mb-0">
            <Masthead
              eyebrow="companion"
              meta="gaussian splat / velocidrone"
              status={<span className="text-muted-foreground">tweak</span>}
            />
          </div>
          <div className="chrome-scroll -mx-6 min-h-0 flex-1 overflow-y-auto px-6 py-6 md:-mx-8 md:px-8">
            <Tweak track={null} />
          </div>
        </div>
      </div>
    )
  }

  return (
    // Dragging across a native window should pan or click, not paint a text selection
    // the way a browser page does - so the shell defaults to non-selectable and each
    // part opts specific text back in (the LAN address, the log, an input's own value).
    // This div, not chrome.tsx, is where that default belongs: chrome.tsx's Masthead is
    // shared with the public site, which is an ordinary web page and stays selectable.
    <div data-testid="companion-shell" className="text-foreground select-none">
      <ParticleField />
      <DragStrip />
      {/* Three bands: a header and a footer that never move, and a scroll box between
          them. Nothing is painted behind the header or the footer - the particle field
          is the window's ground and a band of any colour over it read as a slab, opaque
          or translucent. Instead the scroll box masks its own content out (chrome-scroll)
          as it nears either band, so rows dissolve before they reach the masthead or
          Fly rather than being covered by them. The column is the window's height for
          exactly this: the box, not the page, is what scrolls. */}
      <div className="relative mx-auto flex h-svh w-full max-w-[44rem] flex-col px-6 md:px-8">
        <div className="shrink-0 pt-7 [&>header]:mb-0">
          <Masthead
            eyebrow="companion"
            meta="gaussian splat / velocidrone"
            status={
              // While something is running this is the one place a person is already
              // looking, so it says what rather than staying on the old verdict. Otherwise
              // the verdict carries the newest log line beside it: the log used to live on
              // one tab while the buttons that write to it lived on the others, so a
              // failure landed where nobody was looking. Pressing it opens the whole log.
              state?.busy ? (
                // The job's name, not its percentage: the number lives on the row it
                // is about, beside the ring, where the person who pressed Get is looking.
                <span className="min-w-0 animate-pulse truncate text-signal">◐ {state.busy}</span>
              ) : (
                <button
                  type="button"
                  onClick={() => setShowLog((v) => !v)}
                  aria-expanded={showLog}
                  aria-label={showLog ? 'hide the log' : 'show the log'}
                  className="flex min-w-0 items-baseline gap-3 text-right"
                >
                  <span className={state?.ready ? 'text-live' : 'text-muted-foreground'}>
                    {state?.ready ? '● ready' : '○ setup'}
                  </span>
                  {latest ? (
                    <span className="min-w-0 truncate text-muted-foreground normal-case tracking-normal">
                      {latest}
                    </span>
                  ) : null}
                </button>
              )
            }
          />
        </div>
        <div className="chrome-scroll -mx-6 min-h-0 flex-1 overflow-y-auto px-6 py-6 md:-mx-8 md:px-8">
          {/* Scrolls with the box, on purpose: the strip is visited once and then only
            when something is wrong, and pinning it ate a third of the window. */}
          <SetupStrip state={state} />
          {showLog && log.length ? (
            // select-text: this is what a person copies into a bug report when something
            // goes wrong - collect-mac-diagnostics.sh exists because getting logs out of
            // people matters. Set once here rather than per <li>: user-select inherits down.
            <ol
              data-testid="log"
              className="mt-4 max-h-56 overflow-y-auto border-b border-rule pb-4 font-mono text-[11px] leading-relaxed text-muted-foreground select-text"
            >
              {log.map((line, i) => (
                <li key={i} className="break-all">
                  {line}
                </li>
              ))}
            </ol>
          ) : null}
          <div className="pt-6">
            {tweak ? (
              <Tweak track={tweak} onBack={() => setTweak(null)} />
            ) : (
              <Tracks
                state={state}
                busy={opBusy}
                picked={picked}
                onPickedDone={() => setPicked(null)}
                onTweak={setTweak}
                q={q}
                onSearch={setQ}
              />
            )}
          </div>
        </div>
        {/* Fixture of the shell rather than of the table: flying is not specific to any
            row, and Add track is the way in, which must not scroll away with the first
            screen of rows. */}
        <div className="shrink-0 pb-7">
          {tweak ? (
            // Where Fly sits otherwise: the game is already running while this screen
            // is up, so Fly would be dead anyway, and the way back cannot be missed here.
            <Button
              size="lg"
              variant="outline"
              onClick={() => setTweak(null)}
              className="h-14 w-full font-mono text-base tracking-[0.3em] uppercase"
            >
              ← tracks
            </Button>
          ) : (
            <>
              <div className="mb-4">
                <TracksToolbar state={state} busy={opBusy} picked={picked} />
              </div>
              <Button
                size="lg"
                disabled={!game || busy}
                onClick={() => send('fly')}
                className="h-14 w-full font-mono text-base tracking-[0.3em] uppercase"
              >
                Fly
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
  )
}

/**
 * The top 28px of the window, where macOS draws the traffic lights over the page.
 *
 * The title bar is an overlay (tauri.conf.json: titleBarStyle Overlay, hiddenTitle), so
 * the page's own background runs to the top edge. The stock bar was not a colour the app
 * chose, and it changed as the list scrolled under it - macOS tints a title bar once
 * content moves beneath it - which read as the header flickering. With the overlay
 * nothing is drawn there but the three buttons. What is lost is the bar's drag handle,
 * so this strip puts one back: Tauri's own handler picks up mousedown on any element
 * carrying data-tauri-drag-region (the exact target, not an ancestor, hence a dedicated
 * element rather than the attribute on the masthead), and double-click toggles zoom
 * the way the real bar does. Fixed to the window, not the centered column, so the whole
 * width drags. Above the masthead in z so the top 28px are always the handle; the
 * masthead's own content starts below that line (its pt-7). Not rendered for the
 * browser page, which has no window.
 */
function DragStrip() {
  return <div data-tauri-drag-region className="fixed inset-x-0 top-0 z-20 h-7" />
}
