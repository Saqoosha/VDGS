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

export function Section({
  n,
  label,
  children,
}: {
  n: string
  label: string
  children: ReactNode
}) {
  return (
    <section className="mt-9 border-t border-rule pt-4 first:mt-0 first:border-t-0 first:pt-0">
      <div className="mb-3 flex items-baseline gap-3 font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
        <span className="text-signal">{n}</span>
        <span>{label}</span>
        <span className="h-px flex-1 bg-rule" />
      </div>
      {children}
    </section>
  )
}
