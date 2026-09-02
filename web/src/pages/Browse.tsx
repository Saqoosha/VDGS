import { useEffect, useState, type ReactNode } from 'react'
import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { formatBytes } from '../format'
import { how, type Lang } from '../i18n'

/**
 * The public list of captures, on the web rather than in the app.
 *
 * A browser cannot install anything into a game, so this does not pretend to: it says
 * what exists, what it costs and what it is licensed as, hands over the files, and points
 * at the app for the part a page cannot do.
 *
 * "Scans" rather than "captures": the second is the word this project uses among itself
 * and says nothing to someone arriving from outside. Only the wording changes - the
 * catalog still calls them scenes, because that is the shape of the published data.
 *
 * Only the instructions below carry a translation. The list is names, numbers and
 * licences, and "4,508,391 splats" reads the same in either language.
 */
type Build = { version: string; url: string; bytes: number }
type Apps = { windows?: Build; macos?: Build }

type Published = {
  id: string
  name: string
  description?: string | null
  author?: string | null
  licence?: string | null
  captured?: string | null
  splats: number
  scene: { url: string; bytes: number }
  track?: { url: string; bytes: number; name: string } | null
}

export default function Browse({ lang }: { lang: Lang }) {
  const t = how[lang]
  const ja = lang === 'ja'
  // Japanese runs a step larger in the small ranks, for the same reason.
  const note = ja
    ? 'font-mono text-[12px] leading-relaxed text-muted-foreground'
    : 'font-mono text-[11px] leading-relaxed text-muted-foreground'
  const [scenes, setScenes] = useState<Published[] | null>(null)
  const [apps, setApps] = useState<Apps | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [q, setQ] = useState('')

  useEffect(() => {
    fetch('catalog.json', { cache: 'no-store' })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error('catalog.json: ' + r.status))))
      .then((c) => {
        setScenes(c.scenes ?? [])
        setApps(c.app ?? null)
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'could not load the catalog'))
  }, [])

  const shown = (scenes ?? []).filter((s) =>
    s.name.toLowerCase().includes(q.trim().toLowerCase()),
  )

  return (
    <div>
      <Section n="01" label="scans" flush>
        <label className="flex items-end gap-4 border-b border-rule pb-1.5">
          <span className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
            find
          </span>
          <Input
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="name…"
            aria-label="Search scans"
            className="h-8 border-0 bg-transparent px-0 font-serif text-xl shadow-none focus-visible:ring-0"
          />
        </label>

        {error ? (
          <p className="mt-10 font-mono text-[11px] leading-relaxed text-destructive">{error}</p>
        ) : scenes === null ? (
          <p className="mt-10 font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            loading…
          </p>
        ) : !shown.length ? (
          <p className="mt-10 font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            {scenes.length ? 'no matches' : 'nothing published yet'}
          </p>
        ) : (
          <ol className="mt-1">
            {shown.map((s, i) => (
              <Row key={s.id} index={String(i + 1).padStart(2, '0')} scene={s} />
            ))}
          </ol>
        )}
      </Section>

      {/* lang on this section only - it is the one part that changes. Japanese gets its
          own spacing here: the micro-caps tracking that suits Latin small text pulls
          kanji strokes into each other, and there is no CJK webfont to load (a face
          would be megabytes), so the system's own is what has to stay legible. */}
      <Section
        n="02"
        lang={lang}
        label={ja ? <span className="text-[11px] tracking-normal">{t.label}</span> : t.label}
      >
        <ol className={
          (ja ? 'text-[15px] ' : 'text-[14px] ') +
          'space-y-3 leading-relaxed text-muted-foreground'
        }>
          {/* The app's own names stay in English in both languages, because the app is -
              and they are marked as English, because a section that says it is Japanese
              hands them to a Japanese voice otherwise. Same reason the document is not.
              Step 01 differs per platform; both are listed so a wrong OS guess never sends
              someone to a download that cannot run. */}
          {/* One step, two platforms. Numbering them both 01 read as a mistake, and
              giving the Mac its own number would say there are five steps when there are
              four - the count is what tells a reader how long this is going to take. */}
          <Step n="01">
            <span className="block">
              {t.windows}
              {': '}
              {t.step1a}
              <Name>VDGS.exe</Name>
              {t.step1b}
              <Name>More info</Name>
              {t.step1c}
              <Name>Run anyway</Name>
              {t.step1d}
            </span>
            <span className="mt-1 block">
              {t.macos}
              {': '}
              {t.macStep1a}
              <Name>VDGS Companion</Name>
              {t.macStep1b}
              <Name>Applications</Name>
              {t.macStep1c}
            </span>
          </Step>
          <Step n="02">
            {t.step2a}
            <Name>Install mod</Name>
            {t.step2b}
            <Name>Change</Name>
            {t.step2c}
          </Step>
          <Step n="03">
            {t.step3a}
            <Name>02 get</Name>
            {t.step3b}
          </Step>
          <Step n="04">
            {t.step4a}
            <Name>Fly</Name>
            {t.step4b}
          </Step>
        </ol>

        {/* Both platforms side by side when present. No OS sniffing: a wrong guess is a
            download that cannot run, and the cost of showing two buttons is nil. */}
        <div className="mt-6 flex flex-wrap items-start gap-6">
          {apps?.windows ? (
            <div className="flex flex-col gap-2">
              <Button
                size="lg"
                // Latin micro-caps tracking on a Japanese label spaces kanji apart the way
                // the section heading did before it was fixed - and this is the page's
                // biggest button.
                className={ja ? 'font-mono' : 'font-mono tracking-[0.2em] uppercase'}
                asChild
              >
                <a href={apps.windows.url}>
                  {t.download} {t.windows}
                </a>
              </Button>
              <span className="font-mono text-[11px] text-muted-foreground">
                {apps.windows.version}
                <span className="mx-2 text-rule">/</span>
                {formatBytes(apps.windows.bytes) ?? '—'}
                <span className="mx-2 text-rule">/</span>
                {t.windows}
              </span>
            </div>
          ) : null}
          {apps?.macos ? (
            <div className="flex flex-col gap-2">
              <Button
                size="lg"
                className={ja ? 'font-mono' : 'font-mono tracking-[0.2em] uppercase'}
                asChild
              >
                <a href={apps.macos.url}>
                  {t.download} {t.macos}
                </a>
              </Button>
              <span className="font-mono text-[11px] text-muted-foreground">
                {apps.macos.version}
                <span className="mx-2 text-rule">/</span>
                {formatBytes(apps.macos.bytes) ?? '—'}
                <span className="mx-2 text-rule">/</span>
                {t.macos}
              </span>
            </div>
          ) : null}
        </div>

        <p className={'mt-5 ' + note}>
          {t.trueLensA}
          <Name>True Lens</Name>
          {t.trueLensB}
        </p>
        <p className={'mt-2 ' + note}>
          {t.d3d12}
        </p>
        {/* The app fetches this itself; the link is for anyone who would rather see what
            they are installing, or who is doing it by hand. */}
        <p className={'mt-2 ' + note}>
          {t.loaderA}
          <a
            href="https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5"
            className="text-foreground underline underline-offset-4 hover:text-signal"
          >
            win_x64
          </a>
          {t.loaderMid}
          {/* Not the official macOS build: that one's preloader dies on arm64 before it
              loads anything, so the Mac companion installs a patched fork instead. */}
          <a
            href="https://github.com/Saqoosha/BepInEx/releases/tag/v5.4.23.5-vdgs.1"
            className="text-foreground underline underline-offset-4 hover:text-signal"
          >
            macos_universal
          </a>
          {t.loaderB}
        </p>
      </Section>
    </div>
  )
}

/**
 * A name the app itself uses, in English whatever the surrounding language is - so it is
 * marked English, or a Japanese section reads it aloud in a Japanese voice.
 */
function Name({ children }: { children: ReactNode }) {
  return (
    <b lang="en" className="text-foreground">
      {children}
    </b>
  )
}

/**
 * The number sits beside the text rather than above it, and stays put when a step runs to
 * more than one line - step 01 carries a line per platform, and an inline number would
 * otherwise leave the second line hanging under the digit.
 */
function Step({ n, children }: { n: string; children: ReactNode }) {
  return (
    <li className="flex gap-3">
      <span className="shrink-0 font-mono text-[11px] leading-[1.9] text-signal">{n}</span>
      <span>{children}</span>
    </li>
  )
}

function Row({ index, scene }: { index: string; scene: Published }) {
  return (
    <li className="grid grid-cols-[2.25rem_minmax(0,1fr)] items-start gap-3 border-b border-rule/80 py-4">
      <span className="pt-1 font-mono text-[11px] text-muted-foreground">{index}</span>
      <div className="min-w-0">
        <p className="font-serif text-[1.65rem] leading-tight font-light">{scene.name}</p>
        {scene.description ? (
          <p className="mt-1 text-[13px] leading-snug text-muted-foreground">
            {scene.description}
          </p>
        ) : null}
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
          {scene.splats ? scene.splats.toLocaleString() : '—'} splats
          <span className="mx-2 text-rule">/</span>
          {formatBytes(scene.scene.bytes) ?? '—'}
          {scene.author ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {scene.author}
            </>
          ) : null}
          {scene.licence ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {scene.licence}
            </>
          ) : null}
        </p>
      </div>

    </li>
  )
}
