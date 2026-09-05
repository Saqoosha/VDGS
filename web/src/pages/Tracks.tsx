import { useEffect, useState } from 'react'
import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { formatBytes } from '../format'
import { filterByName } from '../search'
import { send } from '../bridge'
import type { CatalogEntry, CatalogState, SetupState, TrackEntry } from '../types'

/**
 * Tab 02: one table, one row per track.
 *
 * Before this, a capture appeared in three places (the catalog, the track list, the
 * unbound-captures note) and a track appeared in another two - and the only place that
 * could fix a bad binding was one nobody knew was there. A player counts tracks, not
 * captures: a catalog entry is a track that has not landed on this machine yet, and once
 * it has, its row's action follows from what state it is actually in.
 */
type Row =
  | ({ kind: 'catalog' } & CatalogEntry)
  // catalogId is the id to hand `get` when the capture is not on this machine yet.
  | ({ kind: 'track' } & TrackEntry & { catalogId?: string })

function rowName(row: Row): string {
  return row.kind === 'catalog' ? row.name : row.track
}

/**
 * `id` and `installAs` are different namespaces: `id` is what `get` resolves a catalog
 * entry by, `installAs` is the capture directory name it writes to disk and the value a
 * binding stores as `TrackEntry.capture`. Sending the capture name where `get` expects an
 * id looks fine and does nothing - the host's `find` on `id` comes back empty and the
 * download never starts, silently. So a track's missing-capture row can only offer Get
 * when a catalog entry's `installAs` actually matches the capture it is missing; with no
 * catalog loaded, or no entry whose `installAs` matches, there is genuinely nothing to
 * fetch and the row gets no action at all rather than a button that does nothing.
 */
function resolveCatalogId(capture: string | null, catalog: CatalogState | null): string | undefined {
  if (!capture || !catalog) return undefined
  return catalog.entries.find((e) => e.installAs === capture)?.id
}

function rowKey(row: Row): string {
  return row.kind === 'catalog' ? `catalog:${row.id}` : `track:${row.track}`
}

export default function Tracks({
  state,
  busy,
}: {
  state: SetupState | null
  busy: boolean
}) {
  const [q, setQ] = useState('')
  const game = state?.game ?? null
  const tracks = state?.tracks ?? []
  const unbound = state?.unbound ?? []
  const catalog = state?.catalog ?? null
  // Add track, Get and Remove all touch a file the game holds open (user11.db, or a
  // capture on disk) and need it closed - the same guard `run_busy` enforces on the Rust
  // side. Unbind only writes bindings.json, which is ours, not the game's, so it is
  // deliberately left off this fold (see Actions below) and gated on `busy` alone.
  const fileBusy = busy || !!state?.running

  // Fetched once, on the way in: the catalog is only of interest here, and a machine
  // with no network should not greet every tab with an error.
  useEffect(() => {
    if (!catalog && !busy) send('refreshCatalog')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const trackRows: Row[] = tracks.map((t) => ({
    kind: 'track',
    ...t,
    catalogId: resolveCatalogId(t.capture, catalog),
  }))
  // A catalog entry already claimed by a track - installed, or already the Get target of
  // that track's own row above - is not listed again on its own: it is either here or
  // waiting, and a track row already says which. Only entries no track points at yet
  // show up as their own "available" row.
  const claimed = new Set(
    trackRows.flatMap((r) => (r.kind === 'track' && r.catalogId ? [r.catalogId] : [])),
  )
  const catalogRows: Row[] = (catalog?.entries ?? [])
    .filter((e) => !e.installed && !claimed.has(e.id))
    .map((e) => ({ kind: 'catalog', ...e }))
  const rows = [...trackRows, ...catalogRows].sort((a, b) =>
    rowName(a).toLowerCase().localeCompare(rowName(b).toLowerCase()),
  )
  const shown = filterByName(
    rows.map((row) => ({ row, name: rowName(row) })),
    q,
  ).map((r) => r.row)

  return (
    <Section n="01" label="tracks" flush>
      <label className="mb-3 flex items-end gap-4 border-b border-rule pb-1.5">
        <span className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
          find
        </span>
        <Input
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder="name…"
          aria-label="Search tracks"
          className="h-8 border-0 bg-transparent px-0 font-serif text-xl shadow-none focus-visible:ring-0"
        />
      </label>

      <div>
        {!rows.length ? (
          <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            {game ? 'nothing to show yet' : 'no game folder'}
          </p>
        ) : !shown.length ? (
          <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            no matches
          </p>
        ) : (
          <ol>
            {shown.map((row, i) => (
              <TrackRow
                key={rowKey(row)}
                index={String(i + 1).padStart(2, '0')}
                row={row}
                busy={busy}
                fileBusy={fileBusy}
              />
            ))}
          </ol>
        )}

        {unbound.length ? (
          <p className="mt-5 font-mono text-[11px] leading-relaxed text-muted-foreground">
            installed, on no track: {unbound.map((c) => c.name).join(' · ')}
          </p>
        ) : null}

        {catalog?.error ? (
          <p className="mt-5 font-mono text-[11px] leading-relaxed text-destructive">
            {catalog.error}
          </p>
        ) : null}
      </div>

      <div className="mt-4 flex flex-wrap items-center gap-3">
        <Button variant="outline" disabled={!game || fileBusy} onClick={() => send('addTrack')}>
          Add track
        </Button>
        <Button variant="outline" disabled={fileBusy} onClick={() => send('refreshCatalog')}>
          Refresh
        </Button>
        {catalog ? (
          <span className="font-mono text-[11px] break-all text-muted-foreground">
            {catalog.url}
          </span>
        ) : null}
      </div>
    </Section>
  )
}

function TrackRow({
  index,
  row,
  busy,
  fileBusy,
}: {
  index: string
  row: Row
  busy: boolean
  fileBusy: boolean
}) {
  return (
    <li className="group/row grid grid-cols-[2.25rem_minmax(0,1fr)_auto] items-start gap-3 border-b border-rule/80 py-4 last:border-b-0">
      <span className="pt-1 font-mono text-[11px] text-muted-foreground">{index}</span>
      <RowBody row={row} />
      <Actions row={row} busy={busy} fileBusy={fileBusy} />
    </li>
  )
}

function RowBody({ row }: { row: Row }) {
  if (row.kind === 'catalog') {
    return (
      <div className="min-w-0">
        <p className="font-serif text-[1.65rem] leading-tight font-light">{row.name}</p>
        {row.description ? (
          <p className="mt-1 text-[13px] leading-snug text-muted-foreground">
            {row.description}
          </p>
        ) : null}
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
          {row.splats ? row.splats.toLocaleString() : '—'} splats
          <span className="mx-2 text-rule">/</span>
          {formatBytes(row.bytes) ?? '—'}
          {row.author ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {row.author}
            </>
          ) : null}
          {row.licence ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {/* Whether it can be reused at all is the licence's question, and it is
                  the one thing about a capture nobody can work out by looking at it. */}
              {row.licence}
            </>
          ) : null}
        </p>
      </div>
    )
  }

  return (
    <div className="min-w-0">
      <div className="flex flex-wrap items-baseline gap-3">
        <p className="font-serif text-[1.65rem] leading-tight font-light">{row.track}</p>
        {!row.inGame ? (
          // A binding whose track is not in the database shows nothing and says nothing,
          // in the game or here, unless it is called out.
          <span className="font-mono text-[10px] tracking-[0.2em] text-muted-foreground uppercase">
            not in velocidrone
          </span>
        ) : null}
      </div>
      {row.captureInstalled ? (
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
          {row.capture}
          <span className="mx-2 text-rule">/</span>
          {row.splats ? row.splats.toLocaleString() : '—'} splats
          <span className="mx-2 text-rule">/</span>
          {/* A .ply is read and converted every time the capture is shown, which is
              seconds of stutter a converted directory does not cost. */}
          {row.converted ? 'converted' : 'ply'}
          {formatBytes(row.bytes) ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {formatBytes(row.bytes)}
            </>
          ) : null}
          <span className="mx-2 text-rule">/</span>
          {/* Without a mesh the capture is flown straight through, and nothing in the
              game says so - which is why it is stated either way. */}
          {row.collision ? 'collision' : 'no collision'}
        </p>
      ) : (
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-destructive">
          {row.capture ?? 'nothing'} is not installed
        </p>
      )}
    </div>
  )
}

function Actions({ row, busy, fileBusy }: { row: Row; busy: boolean; fileBusy: boolean }) {
  if (row.kind === 'catalog') {
    return (
      <Button disabled={fileBusy} onClick={() => send('get', row.id)}>
        Get
      </Button>
    )
  }
  if (!row.captureInstalled) {
    // A button that fires `get` with an id the host cannot find would look live and do
    // nothing - worse than no button, because nothing tells whoever clicked it that it
    // failed. Offer Get only once a real catalog entry has been resolved.
    return row.catalogId ? (
      <Button disabled={fileBusy} onClick={() => send('get', row.catalogId)}>
        Get
      </Button>
    ) : null
  }
  // Kept quiet until the row is pointed at: this is a list to read, and the button is
  // for the one row in it someone wants gone.
  if (row.fromServer) {
    return (
      <Button
        variant="outline"
        size="sm"
        // busy, not fileBusy: Unbind only writes bindings.json, a file the game never
        // holds open (unbind_track carries no is_running guard on the Rust side, on
        // purpose - the plugin picks the change up from its own poll within a second).
        // Gating it on the game being closed would strand anyone trying to fix a
        // binding for a capture they are actively flying to compare against.
        disabled={busy}
        onClick={() => send('unbindTrack', row.track)}
        className="opacity-0 transition-opacity group-hover/row:opacity-100 focus-visible:opacity-100"
        aria-label={`Unbind ${row.track}`}
      >
        Unbind
      </Button>
    )
  }
  return (
    <Button
      variant="outline"
      size="sm"
      disabled={fileBusy}
      onClick={() => send('removeTrack', row.track)}
      className="opacity-0 transition-opacity group-hover/row:opacity-100 focus-visible:opacity-100"
      aria-label={`Remove ${row.track}`}
    >
      Remove
    </Button>
  )
}
