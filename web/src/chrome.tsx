import type { ReactNode } from 'react'
import { ParticleField } from './ParticleField'
import type { Theme } from './theme'

export function Frame({ theme, children }: { theme: Theme; children: ReactNode }) {
  if (theme === 'survey') {
    return (
      <div className="relative mx-auto max-w-[46rem] px-4 py-8 md:px-6 md:py-12">
        <div className="sheet relative border border-foreground/80 px-7 py-8 md:px-10 md:py-10">
          <CropMarks />
          <div className="pointer-events-none absolute top-0 bottom-0 left-7 w-px bg-signal/80 md:left-10" />
          <div className="pl-4 md:pl-5">{children}</div>
        </div>
      </div>
    )
  }

  return (
    <>
      <ParticleField />
      <div className="relative mx-auto max-w-[44rem] px-6 py-12 md:px-8 md:py-16">
        {children}
      </div>
    </>
  )
}

function CropMarks() {
  const arm = 'pointer-events-none absolute h-3.5 w-3.5 border-signal'
  return (
    <>
      <span className={arm + ' -top-px -left-px border-t-2 border-l-2'} />
      <span className={arm + ' -top-px -right-px border-t-2 border-r-2'} />
      <span className={arm + ' -bottom-px -left-px border-b-2 border-l-2'} />
      <span className={arm + ' -right-px -bottom-px border-b-2 border-r-2'} />
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
