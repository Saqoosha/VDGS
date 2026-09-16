/**
 * A download's progress, in the row it belongs to, the size of the button it replaces.
 *
 * A known percentage is an arc that closes clockwise; a job that cannot say how far
 * along it is - unpacking, importing - gets a third of a ring that turns, which says the
 * app is alive without claiming to know how much is left. Only the number is announced,
 * in the label: Catalog.Download reports every distinct percent, and a live region that
 * repeated each one would speak a hundred times on the way past.
 */
export function Ring({ what, percent }: { what: string; percent: number | null }) {
  const r = 11
  const c = 2 * Math.PI * r
  const known = percent != null
  const dash = known ? (c * Math.min(100, Math.max(0, percent))) / 100 : c / 3
  return (
    <span
      role="progressbar"
      aria-label={what}
      aria-valuenow={known ? percent : undefined}
      aria-valuemin={known ? 0 : undefined}
      aria-valuemax={known ? 100 : undefined}
      title={what}
      className="inline-flex items-center gap-2 text-signal"
    >
      {known ? (
        <span className="font-mono text-[11px] tracking-[0.14em] tabular-nums">{percent}%</span>
      ) : null}
      <svg
        viewBox="0 0 28 28"
        width={28}
        height={28}
        className={known ? '-rotate-90' : 'animate-spin'}
        aria-hidden="true"
      >
        <circle cx={14} cy={14} r={r} fill="none" stroke="currentColor" strokeOpacity={0.2} strokeWidth={2} />
        <circle
          cx={14}
          cy={14}
          r={r}
          fill="none"
          stroke="currentColor"
          strokeWidth={2}
          strokeLinecap="round"
          strokeDasharray={`${dash} ${c}`}
          className={known ? 'transition-[stroke-dasharray] duration-300 ease-out' : undefined}
        />
      </svg>
    </span>
  )
}
