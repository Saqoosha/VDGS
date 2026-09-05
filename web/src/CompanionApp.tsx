import { useEffect, useState, type ReactNode } from 'react'
import { Masthead } from './chrome'
import { ParticleField } from './ParticleField'
import { Button } from '@/components/ui/button'
import { hosted, send, subscribe } from './bridge'
import Own from './pages/Own'
import Setup from './pages/Setup'
import Tracks from './pages/Tracks'
import type { SetupState } from './types'

type TabId = 'setup' | 'tracks' | 'own'

const ALL: { id: TabId; label: string }[] = [
  { id: 'setup', label: '01 setup' },
  { id: 'tracks', label: '02 tracks' },
  { id: 'own', label: '03 create your own' },
]

// Without a host there is no folder picker and no downloader, so the first two tabs
// could only show buttons that do nothing. What is left is the half the plugin serves.
const TABS = hosted ? ALL : ALL.filter((t) => t.id === 'own')

/**
 * The companion app's window. Same shell as the control UI, no router: the app is a
 * window with tabs, not a site with pages.
 */
export default function CompanionApp() {
  const [state, setState] = useState<SetupState | null>(null)
  const [log, setLog] = useState<string[]>([])
  const [tab, setTab] = useState<TabId>(TABS[0].id)

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
  // twice, and can't fly mid-job). Tracks and Own each need the two conditions apart -
  // Tracks' Unbind only writes bindings.json, a file the game never holds open, so it
  // must stay live while the game runs; only its file-touching siblings (Add track, Get,
  // Remove) need the game closed the way Own's ① and ② already do.
  const opBusy = !!state?.busy
  const running = state?.running ?? false
  const busy = running || opBusy
  const game = state?.game ?? null

  // The page scrolls as a whole now, not a box inside it: a box's own scrollbar paints
  // over its content on this WebKit webview (macOS overlay scrollbars ignore
  // scrollbar-gutter, measured - see the commit that changed this), and it appears and
  // disappears with overflow, so switching tabs used to shift everything sideways. The
  // window's scrollbar sits at the window edge instead, outside every page's padding, so
  // it has nothing to overlap. min-h-svh on the column (not a fixed height) is what lets
  // it grow past one screen and still keeps Fly hugging the bottom edge when a tab is
  // short enough to fit without scrolling at all.
  return (
    // Dragging across a native window should pan or click, not paint a text selection
    // the way a browser page does - so the shell defaults to non-selectable and each
    // page opts specific text back in (the LAN address, the log, an input's own value).
    // This div, not chrome.tsx, is where that default belongs: chrome.tsx's Masthead is
    // shared with the public site, which is an ordinary web page and stays selectable.
    <div data-testid="companion-shell" className="text-foreground select-none">
      <ParticleField />
      <div className="relative mx-auto flex min-h-svh w-full max-w-[44rem] flex-col px-6 md:px-8">
        {/* Sticky, not fixed: fixed would need its own width/inset math to stay lined up
            with the centered column, sticky just holds its normal-flow position. The
            fade (chrome-fade-b) keeps the readable band opaque and lets only the
            trailing padding blend back into the canvas - a hard-edged bar would cut a
            rectangle through the drifting gaussians the shell is built around. */}
        <div className="chrome-fade-b sticky top-0 z-10 bg-background pt-7 pb-6">
          <Masthead
            eyebrow="companion"
            nav={
              <>
                {TABS.map((t) => (
                  <Tab key={t.id} now={tab} me={t.id} onPick={setTab}>
                    {t.label}
                  </Tab>
                ))}
              </>
            }
            meta="gaussian splat / velocidrone"
            status={
              // While something is running this is the one place a person is already
              // looking, so it says what rather than staying on the old verdict.
              state?.busy ? (
                <span className="animate-pulse text-signal">
                  ◐ {state.busyPercent != null ? state.busyPercent + '%' : 'working'}
                </span>
              ) : (
                <span className={state?.ready ? 'text-live' : 'text-muted-foreground'}>
                  {state?.ready ? '● ready' : '○ setup'}
                </span>
              )
            }
          />
        </div>
        <div className="flex-1">
          {tab === 'setup' ? (
            <Setup state={state} log={log} />
          ) : tab === 'tracks' ? (
            <Tracks state={state} busy={opBusy} />
          ) : (
            <Own state={state} busy={opBusy} />
          )}
        </div>
        {/* Fixture of the shell rather than of any one page: flying is not specific to
            setup, and a plain browser has no host to fly with at all. chrome-fade-t
            mirrors the masthead's fade but on the leading edge, since this bar is
            approached from above as the page scrolls rather than from below. */}
        {hosted ? (
          <div className="chrome-fade-t sticky bottom-0 z-10 bg-background pt-6 pb-7">
            <Button
              size="lg"
              disabled={!game || busy}
              onClick={() => send('fly')}
              className="h-14 w-full font-mono text-base tracking-[0.3em] uppercase"
            >
              Fly
            </Button>
          </div>
        ) : null}
      </div>
    </div>
  )
}

function Tab({
  now,
  me,
  onPick,
  children,
}: {
  now: TabId
  me: TabId
  onPick: (t: TabId) => void
  children: ReactNode
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={now === me}
      onClick={() => onPick(me)}
      className={
        now === me
          ? 'text-signal underline decoration-signal decoration-2 underline-offset-8'
          : 'text-muted-foreground hover:text-foreground'
      }
    >
      {children}
    </button>
  )
}
