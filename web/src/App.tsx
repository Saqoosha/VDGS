import { NavLink, Outlet } from 'react-router-dom'
import { Frame, Masthead } from './chrome'
import { useStatus } from './useStatus'
import { StatusContext } from './status-context'

const tab = ({ isActive }: { isActive: boolean }) =>
  isActive
    ? 'text-signal underline decoration-signal decoration-2 underline-offset-8'
    : 'text-muted-foreground hover:text-foreground'

export default function App() {
  const status = useStatus()
  return (
    <StatusContext.Provider value={status}>
      <div className="min-h-svh text-foreground">
        <Frame>
          <Masthead
            eyebrow="local · lan :8777"
            nav={
              <>
                <NavLink to="/" end className={tab}>
                  01 control
                </NavLink>
                <NavLink to="/library" className={tab}>
                  02 library
                </NavLink>
              </>
            }
            meta="gaussian splat / velocidrone"
            status={
              <span
                data-testid="live-dot"
                className={status.live ? 'text-live' : 'text-muted-foreground'}
              >
                {status.live ? '● link' : '○ off'}
              </span>
            }
          />
          <Outlet />
        </Frame>
      </div>
    </StatusContext.Provider>
  )
}
