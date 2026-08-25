import { useEffect, useState, type ReactNode } from 'react'
import { Masthead } from './chrome'
import { ParticleField } from './ParticleField'
import { send, subscribe } from './bridge'
import Setup from './pages/Setup'
import Get from './pages/Get'
import type { SetupState } from './types'

/**
 * The companion app's window. Same shell as the control UI, no router: there is one page,
 * and the app is a window rather than a site.
 */
export default function CompanionApp() {
  const [state, setState] = useState<SetupState | null>(null)
  const [log, setLog] = useState<string[]>([])
  // Two views rather than two pages: the app is one window and does not need a router.
  const [tab, setTab] = useState<'setup' | 'get'>('setup')

  useEffect(() => {
    const stop = subscribe((m) => {
      if (m.type === 'state') {
        const { type: _type, ...rest } = m
        setState(rest)
      } else if (m.type === 'progress') {
        setState((prev) => (prev ? { ...prev, busyPercent: m.percent } : prev))
      } else if (m.type === 'busy') {
        setState((prev) => (prev ? { ...prev, busy: m.what, busyPercent: null } : prev))
      } else {
        // Long enough to see what happened, short enough that a session left open does
        // not grow without bound.
        setLog((prev) => [...prev, m.line].slice(-200))
      }
    })
    send('refresh')
    return stop
  }, [])

  // A window, not a page: it is exactly as tall as it is, so the track list takes the
  // slack and Fly sits on the bottom edge instead of below it.
  return (
    <div className="h-svh overflow-hidden text-foreground">
      <ParticleField />
      <div className="relative mx-auto flex h-full w-full max-w-[44rem] flex-col px-6 py-7 md:px-8">
        <Masthead
          eyebrow="companion"
          nav={
            <>
              <Tab now={tab} me="setup" onPick={setTab}>
                01 setup
              </Tab>
              <Tab now={tab} me="get" onPick={setTab}>
                02 get
              </Tab>
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
        {tab === 'setup' ? <Setup state={state} log={log} /> : <Get state={state} />}
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
  now: string
  me: 'setup' | 'get'
  onPick: (t: 'setup' | 'get') => void
  children: ReactNode
}) {
  return (
    <button
      type="button"
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
