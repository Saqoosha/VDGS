import { NavLink, Outlet } from 'react-router-dom'
import { Frame } from './chrome'
import { useStatus } from './useStatus'
import { StatusContext } from './status-context'

export default function App() {
  const status = useStatus()
  return (
    <StatusContext.Provider value={status}>
      <div className="min-h-svh text-foreground">
        <Frame>
          <header className="mb-8">
            <div className="flex items-end justify-between gap-6">
              <div>
                <p className="font-mono text-[10px] tracking-[0.28em] text-muted-foreground uppercase">
                  local · lan :8777
                </p>
                <h1 className="mt-1 font-serif text-[3.4rem] leading-none font-light tracking-tight italic md:text-6xl">
                  VDGS
                </h1>
              </div>
              <nav className="flex gap-5 pb-1 font-mono text-[11px] tracking-[0.2em] uppercase">
                <NavLink
                  to="/"
                  end
                  className={({ isActive }) =>
                    isActive
                      ? 'text-signal underline decoration-signal decoration-2 underline-offset-8'
                      : 'text-muted-foreground hover:text-foreground'
                  }
                >
                  01 control
                </NavLink>
                <NavLink
                  to="/library"
                  className={({ isActive }) =>
                    isActive
                      ? 'text-signal underline decoration-signal decoration-2 underline-offset-8'
                      : 'text-muted-foreground hover:text-foreground'
                  }
                >
                  02 library
                </NavLink>
              </nav>
            </div>
            <div className="mt-5 flex items-baseline justify-between gap-4 border-y border-rule py-1.5 font-mono text-[10px] tracking-[0.18em] uppercase">
              <span className="text-muted-foreground">gaussian splat / velocidrone</span>
              <span
                data-testid="live-dot"
                className={status.live ? 'text-live' : 'text-muted-foreground'}
              >
                {status.live ? '● link' : '○ off'}
              </span>
            </div>
          </header>
          <Outlet />
        </Frame>
      </div>
    </StatusContext.Provider>
  )
}
