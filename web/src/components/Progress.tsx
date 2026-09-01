/**
 * What is happening, and how far along, where someone waiting is already looking.
 *
 * The masthead has carried this since the beginning - ten pixels of mono in the corner
 * of the window - and a capture takes the better part of a minute to arrive. Nobody
 * watches the corner for a minute. This goes at the foot of the list they just pressed
 * a button in.
 *
 * Not every job can say how far along it is: unpacking and importing a track report
 * nothing, and inventing a number for them would be worse than admitting it. Those get a
 * bar that moves without claiming progress.
 */
export function Progress({ what, percent }: { what: string; percent: number | null }) {
  const known = percent != null
  return (
    <div className="mt-3">
      <div className="flex items-baseline justify-between gap-3 font-mono text-[11px] tracking-[0.14em] text-signal uppercase">
        {/* Only the job name is announced. Wrapping the percentage too meant a download
            spoke about a hundred times on the way past - Catalog.Download reports every
            distinct percent - which is a worse thing to do to someone than the corner of
            the window they could not see. The bar's own value carries the number. */}
        <span aria-live="polite" className="min-w-0 truncate">
          <span className="mr-2 animate-pulse">◐</span>
          {what}
        </span>
        {known ? <span className="tabular-nums">{percent}%</span> : null}
      </div>
      <div
        className="mt-1.5 h-1 w-full overflow-hidden rounded-full bg-rule"
        role="progressbar"
        aria-label={what}
        aria-valuenow={known ? percent : undefined}
        aria-valuemin={known ? 0 : undefined}
        aria-valuemax={known ? 100 : undefined}
      >
        {known ? (
          <div
            className="h-full bg-signal transition-[width] duration-300 ease-out"
            style={{ width: percent + '%' }}
          />
        ) : (
          // A third of the track, sliding from just off one end to just off the other:
          // it says the app is alive without claiming to know how much is left. The
          // travel has to be 300%, which is exactly edge to edge - see the keyframes.
          <div className="h-full w-1/3 animate-[progress-sweep_1.4s_ease-in-out_infinite] bg-signal" />
        )}
      </div>
    </div>
  )
}
