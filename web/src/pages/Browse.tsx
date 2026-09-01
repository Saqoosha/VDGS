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
type App = { version: string; url: string; bytes: number }

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
  const [scenes, setScenes] = useState<Published[] | null>(null)
  const [app, setApp] = useState<App | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [q, setQ] = useState('')

  useEffect(() => {
    fetch('catalog.json', { cache: 'no-store' })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error('catalog.json: ' + r.status))))
      .then((c) => {
        setScenes(c.scenes ?? [])
        setApp(c.app ?? null)
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

      <Section n="02" label={t.label}>
        <ol className="space-y-3 text-[14px] leading-relaxed text-muted-foreground">
          <Step n="01">{t.step1}</Step>
          {/* The button names stay in English in both languages, because the app is. */}
          <Step n="02">
            {t.step2a}
            <b className="text-foreground">Install mod</b>
            {t.step2b}
          </Step>
          <Step n="03">
            {t.step3a}
            <b className="text-foreground">02 get</b>
            {t.step3b}
          </Step>
          <Step n="04">
            {t.step4a}
            <b className="text-foreground">Fly</b>
            {t.step4b}
          </Step>
        </ol>

        <div className="mt-6 flex flex-wrap items-center gap-4">
          {app ? (
            <Button size="lg" className="font-mono tracking-[0.2em] uppercase" asChild>
              <a href={app.url}>{t.download}</a>
            </Button>
          ) : null}
          {app ? (
            <span className="font-mono text-[11px] text-muted-foreground">
              {app.version}
              <span className="mx-2 text-rule">/</span>
              {formatBytes(app.bytes) ?? '—'}
              <span className="mx-2 text-rule">/</span>
              windows
            </span>
          ) : null}
        </div>

        <p className="mt-5 font-mono text-[11px] leading-relaxed text-muted-foreground">
          {t.d3d12}
        </p>
        {/* The app fetches this itself; the link is for anyone who would rather see what
            they are installing, or who is doing it by hand. */}
        <p className="mt-2 font-mono text-[11px] leading-relaxed text-muted-foreground">
          {t.loaderA}
          <a
            href="https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5"
            className="text-foreground underline underline-offset-4 hover:text-signal"
          >
            BepInEx 5.4.23.5 (win_x64)
          </a>
          {t.loaderB}
        </p>
      </Section>
    </div>
  )
}

function Step({ n, children }: { n: string; children: ReactNode }) {
  return (
    <li>
      <span className="mr-3 font-mono text-[11px] text-signal">{n}</span>
      {children}
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
