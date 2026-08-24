import { Frame, Masthead } from './chrome'
import Browse from './pages/Browse'

/**
 * The public page. Same shell as the app and the in-game UI - a visitor who later runs
 * the companion should recognise it, because it is the same thing.
 */
export default function Site() {
  return (
    <div className="min-h-svh text-foreground">
      <Frame>
        <Masthead
          eyebrow="3d gaussian splatting / velocidrone"
          meta="captures, tracks and the mod"
          status={
            <a
              href="https://github.com/Saqoosha/VDGS"
              className="text-muted-foreground hover:text-foreground"
            >
              source
            </a>
          }
        />
        <Browse />
      </Frame>
    </div>
  )
}
