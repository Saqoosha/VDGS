import type { ReactNode } from 'react'
import { ParticleField } from './ParticleField'

export function Frame({ children }: { children: ReactNode }) {
  return (
    <>
      <ParticleField />
      <div className="relative mx-auto max-w-[44rem] px-6 py-12 md:px-8 md:py-16">
        {children}
      </div>
    </>
  )
}

/**
 * The masthead, shared by the control UI in the browser and the setup window in the
 * companion app. They are the same tool at different moments - one gets the game ready,
 * the other drives it while flying - so they are not allowed to drift apart visually.
 */
export function Masthead({
  eyebrow,
  nav,
  meta,
  status,
}: {
  eyebrow: string
  nav?: ReactNode
  meta: string
  status: ReactNode
}) {
  return (
    <header className="mb-6">
      <div className="flex items-end justify-between gap-6">
        <div>
          <p className="font-mono text-[10px] tracking-[0.28em] text-muted-foreground uppercase">
            {eyebrow}
          </p>
          <h1 className="mt-1 font-serif text-[3.4rem] leading-none font-light tracking-tight italic md:text-6xl">
            VDGS
          </h1>
        </div>
        {nav ? (
          <nav className="flex gap-5 pb-1 font-mono text-[11px] tracking-[0.2em] uppercase">
            {nav}
          </nav>
        ) : null}
      </div>
      <div className="mt-5 flex items-baseline justify-between gap-4 border-y border-rule py-1.5 font-mono text-[10px] tracking-[0.18em] uppercase">
        <span className="text-muted-foreground">{meta}</span>
        {status}
      </div>
    </header>
  )
}

export function Section({
  n,
  label,
  children,
  className = '',
  flush = false,
  lang,
}: {
  n: string
  /** A node, not a string, so a label in a script that wants different spacing can say so. */
  label: ReactNode
  children: ReactNode
  className?: string
  /** No rule or gap above. For a section that already follows one - the masthead's. */
  flush?: boolean
  /** Set when this section alone is in another language, so readers and search know. */
  lang?: string
}) {
  // Built by swapping the base rather than appending an override: which of two
  // conflicting utilities wins depends on their order in the stylesheet, not here.
  const base = flush
    ? ''
    : 'mt-9 border-t border-rule pt-4 first:mt-0 first:border-t-0 first:pt-0'
  return (
    <section className={base + ' ' + className} lang={lang}>
      <div className="mb-3 flex items-baseline gap-3 font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
        <span className="text-signal">{n}</span>
        <span>{label}</span>
        <span className="h-px flex-1 bg-rule" />
      </div>
      {children}
    </section>
  )
}
