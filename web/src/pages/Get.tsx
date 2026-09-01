import { useEffect } from 'react'
import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { formatBytes } from '../format'
import { Progress } from '../components/Progress'
import { send } from '../bridge'
import type { CatalogEntry, SetupState } from '../types'

/**
 * Captures on offer, and one button to take one.
 *
 * A capture is hundreds of megabytes, so it cannot travel with the mod - and asking
 * someone to find a download, unzip it into the right folder and then write a binding by
 * hand is three chances to get it wrong. Here it is one press.
 */
export default function Get({ state }: { state: SetupState | null }) {
  const busy = (state?.running ?? false) || !!state?.busy
  const catalog = state?.catalog ?? null
  const entries = catalog?.entries ?? []

  // Fetched when this view is first opened rather than at launch: the list is only of
  // interest here, and a machine with no network should not greet everyone with an error.
  useEffect(() => {
    if (!catalog && !busy) send('refreshCatalog')
    // Once, on the way in; Refresh is how it is asked for again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <>
      <Section n="01" label="available" flush className="flex min-h-0 flex-1 flex-col">
        <div className="min-h-0 flex-1 overflow-y-auto pr-1">
          {catalog?.error ? (
            <p className="font-mono text-[11px] leading-relaxed text-destructive">
              {catalog.error}
            </p>
          ) : !entries.length ? (
            <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
              nothing published yet
            </p>
          ) : (
            <ol>
              {entries.map((e, i) => (
                <EntryRow
                  key={e.id}
                  index={String(i + 1).padStart(2, '0')}
                  entry={e}
                  disabled={busy}
                />
              ))}
            </ol>
          )}
        </div>

        {/* At the foot of the list, not against the row that started it: the list
            scrolls, and a capture takes the better part of a minute to arrive. Held
            here so it cannot scroll out from under someone who is waiting on it. */}
        {state?.busy ? <Progress what={state.busy} percent={state.busyPercent} /> : null}

        <div className="mt-4 flex flex-wrap items-center gap-3">
          <Button variant="outline" disabled={busy} onClick={() => send('refreshCatalog')}>
            Refresh
          </Button>
          {catalog ? (
            <span className="font-mono text-[11px] break-all text-muted-foreground">
              {catalog.url}
            </span>
          ) : null}
        </div>
      </Section>
    </>
  )
}

function EntryRow({
  index,
  entry,
  disabled,
}: {
  index: string
  entry: CatalogEntry
  disabled: boolean
}) {
  return (
    <li className="grid grid-cols-[2.25rem_minmax(0,1fr)_auto] items-start gap-3 border-b border-rule/80 py-4 last:border-b-0">
      <span className="pt-1 font-mono text-[11px] text-muted-foreground">{index}</span>
      <div className="min-w-0">
        <p className="font-serif text-[1.65rem] leading-tight font-light">{entry.name}</p>
        {entry.description ? (
          <p className="mt-1 text-[13px] leading-snug text-muted-foreground">
            {entry.description}
          </p>
        ) : null}
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
          {entry.splats ? entry.splats.toLocaleString() : '—'} splats
          <span className="mx-2 text-rule">/</span>
          {formatBytes(entry.bytes) ?? '—'}
          {entry.author ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {entry.author}
            </>
          ) : null}
          {entry.licence ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {/* Whether it can be reused at all is the licence's question, and it is the
                  one thing about a capture nobody can work out by looking at it. */}
              {entry.licence}
            </>
          ) : null}
        </p>
      </div>
      <Button
        variant={entry.installed ? 'ghost' : 'default'}
        disabled={disabled || entry.installed}
        onClick={() => send('get', entry.id)}
      >
        {entry.installed ? 'Installed' : 'Get'}
      </Button>
    </li>
  )
}
