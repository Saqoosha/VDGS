import { NavLink, Outlet } from 'react-router-dom'
import { useStatus } from './useStatus'
import { StatusContext } from './status-context'

export default function App() {
  const status = useStatus()
  return (
    <StatusContext.Provider value={status}>
      <div className="min-h-svh bg-background text-foreground">
        <div className="mx-auto max-w-3xl px-6 py-10">
          <header className="mb-8 flex items-center justify-between gap-4">
            <div className="flex items-center gap-2.5">
              <span
                data-testid="live-dot"
                className={
                  'inline-block size-2 rounded-full ' +
                  (status.live ? 'bg-emerald-600' : 'bg-zinc-300')
                }
              />
              <h1 className="text-xl font-semibold tracking-tight">VDGS</h1>
            </div>
            <nav className="flex gap-5 text-sm">
              <NavLink
                to="/"
                end
                className={({ isActive }) =>
                  isActive
                    ? 'font-medium text-foreground underline decoration-foreground/40 underline-offset-8'
                    : 'text-muted-foreground hover:text-foreground'
                }
              >
                Control
              </NavLink>
              <NavLink
                to="/library"
                className={({ isActive }) =>
                  isActive
                    ? 'font-medium text-foreground underline decoration-foreground/40 underline-offset-8'
                    : 'text-muted-foreground hover:text-foreground'
                }
              >
                Library
              </NavLink>
            </nav>
          </header>
          <Outlet />
        </div>
      </div>
    </StatusContext.Provider>
  )
}
