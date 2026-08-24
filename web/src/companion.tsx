import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/fraunces/standard.css'
import '@fontsource-variable/fraunces/standard-italic.css'
import '@fontsource-variable/ibm-plex-sans/wght.css'
import '@fontsource/ibm-plex-mono/400.css'
import '@fontsource/ibm-plex-mono/500.css'
import { Frame, Masthead } from './chrome'
import { send, subscribe } from './bridge'
import Setup from './pages/Setup'
import type { SetupState } from './types'
import './index.css'

/**
 * The companion app's window. Same shell as the control UI, no router: there is one page,
 * and the app is a window rather than a site.
 */
function Companion() {
  const [state, setState] = useState<SetupState | null>(null)
  const [log, setLog] = useState<string[]>([])

  useEffect(() => {
    const stop = subscribe((m) => {
      if (m.type === 'state') {
        const { type: _type, ...rest } = m
        setState(rest)
      } else {
        // Long enough to see what happened, short enough that a session left open does
        // not grow without bound.
        setLog((prev) => [...prev, m.line].slice(-200))
      }
    })
    send('refresh')
    return stop
  }, [])

  return (
    <div className="min-h-svh text-foreground">
      <Frame compact>
        <Masthead
          eyebrow="companion · setup"
          meta="gaussian splat / velocidrone"
          status={
            <span className={state?.ready ? 'text-live' : 'text-muted-foreground'}>
              {state?.ready ? '● ready' : '○ setup'}
            </span>
          }
        />
        <Setup state={state} log={log} />
      </Frame>
    </div>
  )
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Companion />
  </StrictMode>,
)
