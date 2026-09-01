import { useEffect, useState } from 'react'
import { Frame, Masthead } from './chrome'
import Browse from './pages/Browse'
import { initialLang, rememberLang, type Lang } from './i18n'

/**
 * The public page. Same shell as the app and the in-game UI - a visitor who later runs
 * the companion should recognise it, because it is the same thing.
 *
 * Named apart from its entry point on purpose: site.tsx and Site.tsx are one file on a
 * case-insensitive disk, and writing both silently left only the second.
 */
export default function SiteApp() {
  // Held here and passed down rather than put in a context: there is one consumer.
  const [lang, setLang] = useState<Lang>(initialLang)

  // For readers and for search, which both take the page's word for what language it is.
  useEffect(() => {
    document.documentElement.lang = lang
  }, [lang])

  function choose(next: Lang) {
    setLang(next)
    rememberLang(next)
  }

  return (
    <div className="min-h-svh text-foreground">
      <Frame>
        <Masthead
          eyebrow="3d gaussian splatting / velocidrone"
          meta="scans, tracks and the mod"
          status={
            <span className="flex items-baseline gap-3">
              <LangPick now={lang} onPick={choose} />
              <span className="text-rule">/</span>
              <a
                href="https://github.com/Saqoosha/VDGS"
                className="text-muted-foreground hover:text-foreground"
              >
                source
              </a>
            </span>
          }
        />
        <Browse lang={lang} />
      </Frame>
    </div>
  )
}

/**
 * Both languages are always on screen rather than one toggle that says the other name.
 * A reader who cannot read the current language cannot read a button labelled in it.
 */
function LangPick({ now, onPick }: { now: Lang; onPick: (l: Lang) => void }) {
  return (
    <span className="flex items-baseline gap-2">
      {(['en', 'ja'] as Lang[]).map((l) => (
        <button
          key={l}
          type="button"
          onClick={() => onPick(l)}
          aria-pressed={now === l}
          className={
            now === l ? 'text-signal' : 'text-muted-foreground hover:text-foreground'
          }
        >
          {l}
        </button>
      ))}
    </span>
  )
}
