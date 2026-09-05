import { useEffect, useState } from 'react'
import QRCode from 'qrcode'
import { hosted, send } from '../bridge'
import { Section } from '../chrome'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { useStatus } from '../useStatus'
import Control from './Control'
import type { Capture, SetupState } from '../types'

function defaultTrackName(capture: string): string {
  return capture ? `VDGS ${capture}` : ''
}

/**
 * Why ① and ② are off right now, or null when they are not. One reason, shown once: a
 * duplicate "close VelociDrone first" under both the ply button and the create-track
 * button would be the same sentence twice on one screen, not two explanations.
 *
 * Called only while `hosted` (① and ② never render otherwise), but `state` itself can
 * still be null for a moment there - the very first render, before the host's first push
 * has landed. That used to fall through to `null` here, which read as "nothing to
 * explain" and left the button disabled with no reason on screen at all.
 */
function prepareReason(state: SetupState | null): string | null {
  if (!state) return 'loading…'
  if (state.running)
    return 'close VelociDrone first — adding a capture and creating a track both need the files free'
  if (state.busy) return 'busy — wait for the current job to finish'
  if (!state.game) return 'no game folder — set one up in tab 01 first'
  return null
}

/**
 * Three steps in the order they have to happen, and two different things decide which of
 * them is live.
 *
 * ① and ② need a host: they pick a file and write a database row on this machine, which
 * only the companion (`hosted`) can do - opened in a plain browser, off the plugin's own
 * page, they could not work even in principle, so they do not render at all rather than
 * sitting there disabled with no way to ever turn on.
 *
 * ③ needs the plugin's own HTTP server to answer, which is a different question from
 * whether the game process happens to be running: this same component is *itself served
 * by that server* when opened unhosted, so there the tuning half is live by definition
 * the moment the page loads, no host-reported `running` involved. `useStatus().live` -
 * "did `/api/status` just answer" - is the one signal true in both places, so it is what
 * gates this section in both builds; `state.running` (a field the browser path never
 * even receives - see bridge.ts's dev-only push) only matters to the LAN block, an
 * affordance the companion offers about a *different* screen.
 */
export default function Own({ state, busy }: { state: SetupState | null; busy: boolean }) {
  const { state: plugin, live, refresh } = useStatus()
  const running = !!state?.running
  const prepare = !running && !busy && !!state?.game
  const unbound = state?.unbound ?? []
  const reason = prepareReason(state)

  return (
    <div>
      {hosted ? (
        <>
          {reason ? (
            <p className="mb-6 font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
              {reason}
            </p>
          ) : null}

          <Section n="01" label="add a capture" flush>
            <p className="text-[14px] leading-relaxed text-foreground/90">
              Copy a .ply straight into the game — the plugin reads it at load time, no
              conversion step needed.
            </p>
            <div className="mt-4">
              <Button disabled={!prepare} onClick={() => send('installPly')}>
                Add a .ply
              </Button>
            </div>
          </Section>

          <MakeTrack prepare={prepare} unbound={unbound} />
        </>
      ) : null}

      <Section n="03" label="tune it">
        {/* The LAN address is an invitation to a *second* screen - useless where it is
            already being read (the browser path is that second screen already), and
            good for nothing while the game is closed: state.rs reports an address the
            OS could route to regardless of whether the plugin's server exists yet, so
            pairing it with `running` here, not merely `lanUrl`, is what keeps it off a
            screen where scanning it gets connection refused. */}
        {hosted && running && state?.lanUrl ? <LanQr url={state.lanUrl} /> : null}
        {live ? (
          <Control state={plugin} refresh={refresh} />
        ) : (
          <FlyFirst isHosted={hosted} />
        )}
      </Section>
    </div>
  )
}

function FlyFirst({ isHosted }: { isHosted: boolean }) {
  return (
    <p className="font-serif text-xl font-light text-muted-foreground italic">
      {isHosted
        ? 'fly first — this talks to the plugin, and the plugin only exists while VelociDrone is running'
        : 'waiting for the plugin…'}
    </p>
  )
}

function MakeTrack({ prepare, unbound }: { prepare: boolean; unbound: Capture[] }) {
  // The picker and the name field only ever need to agree with each other, not with the
  // host: creating a track is a single fire-and-forget send, and the host pushes whatever
  // it learns back down as a fresh `unbound` list.
  const [capture, setCapture] = useState(unbound[0]?.name ?? '')
  const [name, setName] = useState(defaultTrackName(unbound[0]?.name ?? ''))
  // Once someone has typed their own name, switching the capture selector must not
  // stomp on it - that would be losing a name to a click on an unrelated control.
  const [edited, setEdited] = useState(false)

  useEffect(() => {
    if (unbound.some((c) => c.name === capture)) return
    const next = unbound[0]?.name ?? ''
    setCapture(next)
    if (!edited) setName(defaultTrackName(next))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [unbound])

  const pick = (next: string) => {
    setCapture(next)
    if (!edited) setName(defaultTrackName(next))
  }

  const create = () => {
    send('createTrack', undefined, { name, capture })
  }

  return (
    <Section n="02" label="make a track">
      {unbound.length === 0 ? (
        <p className="font-mono text-[11px] tracking-[0.14em] text-muted-foreground uppercase">
          nothing installed yet — add a capture above first
        </p>
      ) : (
        <>
          <label className="flex items-end gap-4 border-b border-rule pb-1.5">
            <span className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
              capture
            </span>
            <Select value={capture} onValueChange={pick}>
              <SelectTrigger
                aria-label="Capture"
                className="h-8 flex-1 border-0 bg-transparent px-0 font-serif text-xl shadow-none focus-visible:ring-0"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {unbound.map((c) => (
                  <SelectItem key={c.name} value={c.name}>
                    {c.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {/* The only way to take a capture back out. Without this, ① only ever adds -
                every .ply someone tries piles up here, in the ② picker and in tab 02's
                "installed, on no track" line, forever. Gated the same as ① and ②
                themselves: remove_capture refuses while the game is running because the
                file may be open. */}
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={!prepare || !capture}
              onClick={() => send('removeCapture', capture)}
              aria-label={`Remove ${capture}`}
            >
              Remove
            </Button>
          </label>

          <label className="mt-4 flex items-end gap-4 border-b border-rule pb-1.5">
            <span className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
              track name
            </span>
            <Input
              value={name}
              onChange={(e) => {
                setEdited(true)
                setName(e.target.value)
              }}
              aria-label="Track name"
              className="h-8 flex-1 border-0 bg-transparent px-0 font-serif text-xl shadow-none focus-visible:ring-0"
            />
          </label>

          <div className="mt-4">
            <Button disabled={!prepare || !capture || !name.trim()} onClick={create}>
              Create track
            </Button>
          </div>
        </>
      )}
    </Section>
  )
}

/**
 * The one moment a second screen helps: tuning while flying means both hands are on a
 * transmitter, so the controls belong on whatever is nearby, not on the machine running
 * the game. The plugin's own page already works there - this is just the address to it.
 */
function LanQr({ url }: { url: string }) {
  const [dataUrl, setDataUrl] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    QRCode.toDataURL(url, { margin: 1, width: 132 })
      .then((d) => {
        if (!cancelled) setDataUrl(d)
      })
      .catch(() => {
        if (!cancelled) setDataUrl(null)
      })
    return () => {
      cancelled = true
    }
  }, [url])

  return (
    <div className="mb-6 flex flex-wrap items-center gap-4 border-b border-rule pb-6">
      {dataUrl ? (
        // Decorative next to the URL text right beside it - a screen reader gains
        // nothing from "QR code" that the address itself does not already say.
        <img src={dataUrl} alt="" width={88} height={88} className="shrink-0" />
      ) : null}
      <div>
        <p className="font-mono text-[10px] tracking-[0.22em] text-muted-foreground uppercase">
          on another screen
        </p>
        {/* select-text: the whole point of this line is to be typed or copied onto a
            second device, the one thing the QR code next to it cannot do for a laptop. */}
        <p className="mt-1 font-mono text-[13px] text-foreground/90 select-text">{url}</p>
      </div>
    </div>
  )
}
