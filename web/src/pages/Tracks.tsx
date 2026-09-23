import { useEffect, useState } from "react";
import { Section } from "../chrome";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Dialog } from "radix-ui";
import { Progress } from "../components/Progress";
import { Ring } from "../components/Ring";
import { formatBytes } from "../format";
import { filterByName } from "../search";
import { send } from "../bridge";
import { useStatus } from "../useStatus";
import { busyIsSetup } from "../SetupStrip";
import type {
  CatalogEntry,
  CatalogState,
  SetupState,
  TrackEntry,
} from "../types";

/**
 * The track table: one row per track, and everything that happens to a track.
 *
 * Before this, a capture appeared in three places (the catalog, the track list, the
 * unbound-captures note) and a track appeared in another two - and the only place that
 * could fix a bad binding was one nobody knew was there. A player counts tracks, not
 * captures: a catalog entry is a track that has not landed on this machine yet, and once
 * it has, its row's action follows from what state it is actually in.
 */
type Row =
  | ({ kind: "catalog" } & CatalogEntry)
  // catalogId is the id to hand `get` when the capture is not on this machine yet, or
  // again when the catalog has a newer cut of it (update).
  | ({ kind: "track" } & TrackEntry & {
      catalogId?: string;
      update?: boolean;
      // The entry was found by track, not by capture: fetching it has to rebind.
      viaTrack?: boolean;
      // ...and the capture the track is bound to is installed, so the row says Replace.
      replaces?: boolean;
    });

function rowName(row: Row): string {
  return row.kind === "catalog" ? row.name : row.track;
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
function resolveCatalogId(
  captures: string[],
  catalog: CatalogState | null,
): string | undefined {
  if (!captures.length || !catalog) return undefined;
  return catalog.entries.find(
    (e) => !!e.installAs && captures.includes(e.installAs),
  )?.id;
}

/**
 * The catalog's cut of this track when the track is bound to some other capture - one
 * picked by hand, or added from a .ply under the catalog's name. Without this the track
 * showed twice: its own row, and the catalog entry as an "available" row of the same
 * name, and a Get lit both rows' progress because jobs are matched to rows by name.
 */
function resolveByTrack(
  track: string,
  catalog: CatalogState | null,
): string | undefined {
  return catalog?.entries.find((e) => e.track === track)?.id;
}

/**
 * Whether the host's current job is about this row. The host names each job after the
 * row - `downloading <name>` then `installing <name>` for a Get, `removing <name>`,
 * `unbinding <name>` - so an exact match on the phrase is enough; a substring match
 * would light up "FDF" while "FDF night" downloads.
 */
function busyFor(busy: string | null | undefined, name: string): boolean {
  return (
    busy === `downloading ${name}` ||
    busy === `installing ${name}` ||
    busy === `removing ${name}` ||
    busy === `unbinding ${name}`
  );
}

/**
 * The names the host might use for a job about this row. A track row whose missing
 * capture is fetched through `catalogId` is downloaded under the catalog entry's name,
 * not the track's, so both are tried.
 */
function busyNames(row: Row, catalog: CatalogState | null): string[] {
  const names = [rowName(row)];
  if (row.kind === "track" && row.catalogId) {
    const entry = catalog?.entries.find((e) => e.id === row.catalogId);
    if (entry) names.push(entry.name);
  }
  return names;
}

function rowBusy(
  busy: string | null | undefined,
  row: Row,
  catalog: CatalogState | null,
): boolean {
  return busyNames(row, catalog).some((n) => busyFor(busy, n));
}

function rowKey(row: Row): string {
  return row.kind === "catalog" ? `catalog:${row.id}` : `track:${row.track}`;
}

/** The .ply someone just chose, waiting for a track name before anything is written. */
export type Picked = { path: string; stem: string };

export default function Tracks({
  state,
  busy,
  picked,
  onPickedDone,
  onTweak,
  q,
  onSearch,
}: {
  state: SetupState | null;
  busy: boolean;
  picked: Picked | null;
  /** The name dialog is gone - created or cancelled - and the shell should forget the pick. */
  onPickedDone: () => void;
  onTweak: (track: string) => void;
  /** The search text. Owned by the shell, so it survives a trip to the tweak screen. */
  q: string;
  onSearch: (q: string) => void;
}) {
  // Which track the plugin has on screen right now, if it is answering at all. Tweak
  // goes over the plugin's HTTP API and only reaches the loaded capture, so it is
  // offered on exactly that row - a live Tweak on a track that is not loaded would open
  // a screen of controls that move something else.
  const { state: plugin, live } = useStatus(!!state?.running);
  const loadedTrack = live ? (plugin?.track ?? null) : null;
  // Which half of the table to show. Local, not the shell's: unlike the search text it
  // is a glance-and-reset kind of thing, and coming back from Tweak should show all.
  const [only, setOnly] = useState<Only>("all");
  const game = state?.game ?? null;
  const tracks = state?.tracks ?? [];
  const unbound = state?.unbound ?? [];
  const catalog = state?.catalog ?? null;
  // Add track, Get and Remove all touch a file the game holds open (user11.db, or a
  // capture on disk) and need it closed - the same guard each of those jobs enforces on
  // the Rust side. Unbind only writes bindings.json, which is ours, not the game's, so it is
  // deliberately left off this fold (see Actions below) and gated on `busy` alone.
  const fileBusy = busy || !!state?.running;

  // Fetched once, on the way in: the catalog is only of interest here, and a machine
  // with no network should not greet every tab with an error.
  useEffect(() => {
    if (!catalog && !busy) send("refreshCatalog");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const trackRows: Row[] = tracks.map((t) => {
    // `captures` is one name per bound capture; `capture` joins them for display and
    // is the fallback for a host that does not send the list.
    const captures = t.captures ?? (t.capture ? [t.capture] : []);
    const byCapture = resolveCatalogId(captures, catalog);
    const byTrack = byCapture ? undefined : resolveByTrack(t.track, catalog);
    const catalogId = byCapture ?? byTrack;
    return {
      kind: "track",
      ...t,
      catalogId,
      update:
        !!byCapture &&
        !!catalog?.entries.find((e) => e.id === byCapture)?.update,
      viaTrack: !!byTrack,
      replaces: !!byTrack && t.captureInstalled,
    };
  });
  // A catalog entry already claimed by a track - installed, or already the Get target of
  // that track's own row above - is not listed again on its own: it is either here or
  // waiting, and a track row already says which. Only entries no track points at yet
  // show up as their own "available" row.
  const claimed = new Set(
    trackRows.flatMap((r) =>
      r.kind === "track" && r.catalogId ? [r.catalogId] : [],
    ),
  );
  const catalogRows: Row[] = (catalog?.entries ?? [])
    .filter((e) => !e.installed && !claimed.has(e.id))
    .map((e) => ({ kind: "catalog", ...e }));
  // What is on this machine first, then what could be: a Get row between two flyable
  // ones reads as a gap in the list, and the catalog will outgrow the machine's own
  // tracks many times over. Name order within each half.
  const byName = (a: Row, b: Row) =>
    rowName(a).toLowerCase().localeCompare(rowName(b).toLowerCase());
  const rows = [...trackRows.sort(byName), ...catalogRows.sort(byName)];
  const wanted = rows.filter((r) =>
    only === "all"
      ? true
      : only === "installed"
        ? r.kind === "track"
        : r.kind === "catalog",
  );
  const shown = filterByName(
    wanted.map((row) => ({ row, name: rowName(row) })),
    q,
  ).map((r) => r.row);

  return (
    <Section label="tracks">
      <div className="mb-3">
        <FindField q={q} onChange={onSearch} only={only} onOnly={setOnly} />
      </div>

      {/* A job that is about one row shows in that row (see TrackRow); one about the
          setup section shows there. The rest - fetching the catalog, adding a track
          whose row does not exist yet - show here at the head of the list, where the
          button was pressed, rather than in the corner of the window. */}
      {state?.busy &&
      !busyIsSetup(state.busy) &&
      !shown.some((r) => rowBusy(state.busy, r, catalog)) ? (
        <Progress what={state.busy} percent={state.busyPercent} />
      ) : null}

      {picked ? <NameDialog picked={picked} onDone={onPickedDone} /> : null}

      <div>
        {!rows.length && !picked ? (
          <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            {game ? "no tracks yet — add one below" : "no game folder"}
          </p>
        ) : !shown.length && !picked ? (
          <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
            no matches
          </p>
        ) : (
          <ol>
            {shown.map((row) => (
              <TrackRow
                key={rowKey(row)}
                row={row}
                busy={busy}
                fileBusy={fileBusy}
                loaded={row.kind === "track" && row.track === loadedTrack}
                onTweak={onTweak}
                progress={
                  state?.busy && rowBusy(state.busy, row, catalog)
                    ? { what: state.busy, percent: state.busyPercent }
                    : null
                }
              />
            ))}
          </ol>
        )}

        {/* Left behind when Add track fails after the copy, or dropped into vdgs/ by
            hand; a capture nothing points at is invisible everywhere else. Remove is the
            only way it ever leaves. */}
        {unbound.length ? (
          <div className="mt-5 font-mono text-[11px] leading-relaxed text-muted-foreground">
            <span>installed, on no track:</span>
            {unbound.map((c) => (
              <span
                key={c.name}
                className="ml-3 inline-flex items-baseline gap-2"
              >
                {c.name}
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={fileBusy}
                  onClick={() => send("removeCapture", c.name)}
                  aria-label={`Remove ${c.name}`}
                  className="h-5 px-1.5 text-[10px]"
                >
                  remove
                </Button>
              </span>
            ))}
          </div>
        ) : null}

        {catalog?.error ? (
          <p className="mt-5 font-mono text-[11px] leading-relaxed text-destructive">
            {catalog.error}
          </p>
        ) : null}
      </div>
    </Section>
  );
}

/** Which rows to show: everything, only what is on this machine, or only what is not. */
type Only = "all" | "installed" | "available";

const ONLY: Only[] = ["all", "installed", "available"];

/** The field under the section label, with the filter at its end. */
function FindField({
  q,
  onChange,
  only,
  onOnly,
}: {
  q: string;
  onChange: (q: string) => void;
  only: Only;
  onOnly: (o: Only) => void;
}) {
  return (
    <div className="flex items-baseline gap-4 border-b border-rule pb-1.5">
      <label className="flex min-w-0 flex-1 items-baseline gap-4">
        <span className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
          find
        </span>
        <Input
          value={q}
          onChange={(e) => onChange(e.target.value)}
          placeholder="name…"
          aria-label="Search tracks"
          className="h-8 border-0 bg-transparent px-0 font-serif text-xl shadow-none focus-visible:ring-0"
        />
      </label>
      {/* A filter, not tabs: the window had tabs and lost them for splitting one
          thing across screens. This narrows one list in place. */}
      <div
        role="radiogroup"
        aria-label="Show"
        className="flex shrink-0 gap-3 font-mono text-[10px] tracking-[0.22em] uppercase"
      >
        {ONLY.map((o) => (
          <button
            key={o}
            type="button"
            role="radio"
            aria-checked={only === o}
            onClick={() => onOnly(o)}
            className={
              only === o
                ? "text-signal underline decoration-signal/60 underline-offset-4"
                : "text-muted-foreground hover:text-foreground"
            }
          >
            {o}
          </button>
        ))}
      </div>
    </div>
  );
}

/**
 * Add track and Refresh. Rendered by the shell in its fixed footer, above Fly: they are
 * the way in, and the way in must not scroll away.
 */
export function TracksToolbar({
  state,
  busy,
  picked,
}: {
  state: SetupState | null;
  busy: boolean;
  picked: Picked | null;
}) {
  const game = state?.game ?? null;
  const catalog = state?.catalog ?? null;
  const fileBusy = busy || !!state?.running;
  return (
    <div className="flex flex-wrap items-center gap-3">
      {/* The .ply is the whole input: the host opens a picker for it, hands the path
          back as `picked`, and the name dialog takes it from there. Nothing is copied
          until the name is confirmed. */}
      <Button
        variant="outline"
        disabled={!game || fileBusy || !!picked}
        onClick={() => send("pickPly")}
      >
        Add track
      </Button>
      <Button
        variant="outline"
        disabled={fileBusy}
        onClick={() => send("refreshCatalog")}
      >
        Refresh
      </Button>
      {catalog ? (
        <span className="min-w-0 truncate font-mono text-[11px] text-muted-foreground">
          {catalog.url}
        </span>
      ) : null}
    </div>
  );
}

function TrackRow({
  row,
  busy,
  fileBusy,
  loaded,
  onTweak,
  progress,
}: {
  row: Row;
  busy: boolean;
  fileBusy: boolean;
  /** The plugin has this track's capture on screen right now. */
  loaded: boolean;
  onTweak: (track: string) => void;
  /** The host's current job is about this row (see busyFor). */
  progress: { what: string; percent: number | null } | null;
}) {
  return (
    <li className="grid grid-cols-[minmax(0,1fr)_auto] items-start gap-3 border-b border-rule/80 py-4 last:border-b-0">
      <RowBody row={row} />
      <div className="flex items-center gap-2">
        {progress ? (
          // In place of the button that started it: the Get the row offered is what is
          // happening now, and a person watching this row is watching the right place.
          <Ring what={progress.what} percent={progress.percent} />
        ) : null}
        {!progress && row.kind === "track" && row.captureInstalled ? (
          // Live only on the row the plugin is showing. Elsewhere it stays visible but
          // off, with the reason in its title: a button that vanishes teaches nobody
          // that flying is what turns it on.
          <Button
            variant={loaded ? "default" : "outline"}
            size="sm"
            disabled={!loaded}
            title={loaded ? undefined : "fly this track first"}
            onClick={() => onTweak(row.track)}
            aria-label={`Tweak ${row.track}`}
          >
            Tweak
          </Button>
        ) : null}
        {!progress ? (
          <Actions row={row} busy={busy} fileBusy={fileBusy} />
        ) : null}
      </div>
    </li>
  );
}

/**
 * File -> name -> create, the middle step. The pick has already happened; this asks
 * what to call the track and only then sends both together, so cancelling here writes
 * nothing anywhere. The default name is the file's own, prefixed - most people will
 * keep it, and a blank field is the one thing that would stall the flow.
 *
 * A dialog, not a row: it was a row at the head of the table for a while, and with the
 * table scrolled it opened out of view - the file was picked, nothing visible changed,
 * and the person went looking. A modal cannot be missed and cannot be left half-done
 * behind another click; Escape and the backdrop are Cancel.
 */
function NameDialog({
  picked,
  onDone,
}: {
  picked: Picked;
  onDone: () => void;
}) {
  const [name, setName] = useState(`VDGS ${picked.stem}`);
  const create = () => {
    send("addTrack", undefined, { path: picked.path, name });
    onDone();
  };
  return (
    <Dialog.Root open onOpenChange={(open) => !open && onDone()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-background/70 backdrop-blur-sm" />
        <Dialog.Content
          aria-describedby={undefined}
          className="fixed top-1/2 left-1/2 z-50 w-[min(32rem,calc(100vw-3rem))] -translate-x-1/2 -translate-y-1/2 border border-rule bg-background p-6 text-foreground shadow-2xl outline-none"
        >
          <Dialog.Title className="font-mono text-[10px] tracking-[0.22em] text-signal uppercase">
            new track
          </Dialog.Title>
          <p className="mt-2 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
            {picked.stem}.ply
          </p>
          <form
            className="mt-5"
            onSubmit={(e) => {
              e.preventDefault();
              if (name.trim()) create();
            }}
          >
            <label className="flex items-baseline gap-4 border-b border-rule pb-1.5">
              <span className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
                track name
              </span>
              <Input
                autoFocus
                value={name}
                onChange={(e) => setName(e.target.value)}
                aria-label="Track name"
                className="h-8 flex-1 border-0 bg-transparent px-0 font-serif text-xl shadow-none focus-visible:ring-0"
              />
            </label>
            <div className="mt-5 flex justify-end gap-3">
              <Button type="button" variant="ghost" onClick={onDone}>
                Cancel
              </Button>
              <Button type="submit" disabled={!name.trim()}>
                Create
              </Button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function RowBody({ row }: { row: Row }) {
  if (row.kind === "catalog") {
    return (
      <div className="min-w-0">
        <p className="font-serif text-[1.65rem] leading-tight font-light">
          {row.name}
        </p>
        {row.description ? (
          <p className="mt-1 text-[13px] leading-snug text-muted-foreground">
            {row.description}
          </p>
        ) : null}
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-muted-foreground">
          {row.splats ? row.splats.toLocaleString() : "—"} splats
          <span className="mx-2 text-rule">/</span>
          {formatBytes(row.bytes) ?? "—"}
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
    );
  }

  return (
    <div className="min-w-0">
      <div className="flex flex-wrap items-baseline gap-3">
        <p className="font-serif text-[1.65rem] leading-tight font-light">
          {row.track}
        </p>
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
          {row.splats ? row.splats.toLocaleString() : "—"} splats
          <span className="mx-2 text-rule">/</span>
          {/* A .ply is read and converted every time the capture is shown, which is
              seconds of stutter a converted directory does not cost. */}
          {row.converted ? "converted" : "ply"}
          {formatBytes(row.bytes) ? (
            <>
              <span className="mx-2 text-rule">/</span>
              {formatBytes(row.bytes)}
            </>
          ) : null}
          <span className="mx-2 text-rule">/</span>
          {/* Without a mesh the capture is flown straight through, and nothing in the
              game says so - which is why it is stated either way. */}
          {row.collision ? "collision" : "no collision"}
        </p>
      ) : (
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.04em] text-destructive">
          {row.capture ?? "nothing"} is not installed
        </p>
      )}
    </div>
  );
}

function Actions({
  row,
  busy,
  fileBusy,
}: {
  row: Row;
  busy: boolean;
  fileBusy: boolean;
}) {
  if (row.kind === "catalog") {
    return (
      <Button disabled={fileBusy} onClick={() => send("get", row.id)}>
        Get
      </Button>
    );
  }
  if (!row.captureInstalled) {
    // A button that fires `get` with an id the host cannot find would look live and do
    // nothing - worse than no button, because nothing tells whoever clicked it that it
    // failed. Offer Get only once a real catalog entry has been resolved.
    return row.catalogId ? (
      <Button
        disabled={fileBusy}
        onClick={() => send(row.viaTrack ? "replace" : "get", row.catalogId)}
      >
        Get
      </Button>
    ) : null;
  }
  // An update is a get: the folder is swapped for the new cut, the binding and
  // placement.json are left alone. Replace is for a track bound to a capture that is not
  // the catalog's: the catalog's cut is installed and the track is rebound to it, even if
  // that folder was already on disk, and the capture it showed stays installed, unbound.
  const update =
    row.catalogId && (row.update || row.replaces) ? (
      <Button
        size="sm"
        disabled={fileBusy}
        onClick={() => send(row.update ? "get" : "replace", row.catalogId)}
      >
        {row.update ? "Update" : "Replace"}
      </Button>
    ) : null;
  // Always shown, beside Tweak: they used to appear on hover only, which hid one of the
  // row's two actions behind a gesture while the other sat in plain view. Removal asks
  // before it acts, so being visible costs nothing.
  if (row.fromServer) {
    return (
      <>
        {update}
        <Button
          variant="outline"
          size="sm"
          // busy, not fileBusy: Unbind only writes bindings.json, a file the game never
          // holds open (unbind_track carries no is_running guard on the Rust side, on
          // purpose - the plugin picks the change up from its own poll within a second).
          // Gating it on the game being closed would strand anyone trying to fix a
          // binding for a capture they are actively flying to compare against.
          disabled={busy}
          onClick={() => send("unbindTrack", row.track)}
          aria-label={`Unbind ${row.track}`}
        >
          Unbind
        </Button>
      </>
    );
  }
  return (
    <>
      {update}
      <Button
        variant="outline"
        size="sm"
        disabled={fileBusy}
        onClick={() => send("removeTrack", row.track)}
        aria-label={`Remove ${row.track}`}
      >
        Remove
      </Button>
    </>
  );
}
