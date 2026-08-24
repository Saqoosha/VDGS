import { Frame, Masthead } from './chrome'
import Browse from './pages/Browse'

/**
 * The public page. Same shell as the app and the in-game UI - a visitor who later runs
 * the companion should recognise it, because it is the same thing.
 *
 * Named apart from its entry point on purpose: site.tsx and Site.tsx are one file on a
 * case-insensitive disk, and writing both silently left only the second.
 */
export default function SiteApp() {
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
