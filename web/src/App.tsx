import { NavLink, Outlet } from 'react-router-dom'
import { Frame } from './chrome'
import { useStatus } from './useStatus'
import { StatusContext } from './status-context'
import { useTheme, type Theme } from './theme'

export default function App() {
  const status = useStatus()
  const [theme, setTheme] = useTheme()
  return (
    <StatusContext.Provider value={status}>
      <div className="min-h-svh text-foreground">
        <Frame theme={theme}>
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
              <span className="flex items-baseline gap-4">
                <ThemeSwitch theme={theme} onChange={setTheme} />
                <span
                  data-testid="live-dot"
                  className={status.live ? 'text-live' : 'text-muted-foreground'}
                >
                  {status.live ? '● link' : '○ off'}
                </span>
              </span>
            </div>
          </header>
          <Outlet />
        </Frame>
      </div>
    </StatusContext.Provider>
  )
}

function ThemeSwitch({
  theme,
  onChange,
}: {
  theme: Theme
  onChange: (t: Theme) => void
}) {
  const opt = (id: Theme, label: string) => (
    <button
      type="button"
      onClick={() => onChange(id)}
      className={
        'tracking-[0.18em] uppercase ' +
        (theme === id ? 'text-foreground' : 'text-muted-foreground hover:text-foreground')
      }
    >
      {label}
    </button>
  )
  return (
    <span className="flex items-baseline gap-2">
      {opt('survey', 'survey')}
      <span className="text-rule">/</span>
      {opt('particles', 'particles')}
    </span>
  )
}
