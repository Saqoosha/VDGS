import { useEffect, useState, type ReactNode } from 'react'
import { Masthead } from './chrome'
import { ParticleField } from './ParticleField'
import { Button } from '@/components/ui/button'
import { hosted, send, subscribe } from './bridge'
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

  const busy = (state?.running ?? false) || !!state?.busy
  const game = state?.game ?? null

  // A window, not a page: it is exactly as tall as it is, so the tab content takes the
  // slack and Fly sits on the bottom edge instead of below it.
  return (
    <div className="h-svh overflow-hidden text-foreground">
      <ParticleField />
      <div className="relative mx-auto flex h-full w-full max-w-[44rem] flex-col px-6 py-7 md:px-8">
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
        <div className="min-h-0 flex-1 overflow-y-auto">
          {tab === 'setup' ? (
            <Setup state={state} log={log} />
          ) : tab === 'tracks' ? (
            <Tracks state={state} busy={busy} />
          ) : (
            <Placeholder note="create your own — coming soon" />
          )}
        </div>
        {/* Fixture of the shell rather than of any one page: flying is not specific to
            setup, and a plain browser has no host to fly with at all. */}
        {hosted ? (
          <div className="mt-6 shrink-0">
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

function Placeholder({ note }: { note: string }) {
  return (
    <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
      {note}
    </p>
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
