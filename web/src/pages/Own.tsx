import { useEffect, useState } from 'react'
import QRCode from 'qrcode'
import { send } from '../bridge'
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
import Control from './Control'
import type { Capture, SetupState } from '../types'

function defaultTrackName(capture: string): string {
  return capture ? `VDGS ${capture}` : ''
}

/**
 * Why ① and ② are off right now, or null when they are not. One reason, shown once: a
 * duplicate "close VelociDrone first" under both the ply button and the create-track
 * button would be the same sentence twice on one screen, not two explanations.
 */
function prepareReason(state: SetupState | null): string | null {
  if (!state) return null
  if (state.running)
    return 'close VelociDrone first — adding a capture and creating a track both need the files free'
  if (state.busy) return 'busy — wait for the current job to finish'
  if (!state.game) return 'no game folder — set one up in tab 01 first'
  return null
}

/**
 * Three steps in the order they have to happen, and the game's state decides which of
 * them is live.
 *
 * Putting a file into the game folder and writing a row into user11.db both need the game
 * closed. Tuning talks to the plugin's HTTP server, which does not exist unless the game
 * is open. So this page is never all-enabled, and the half that is off says which way to
 * go rather than looking broken.
 */
export default function Own({ state, busy }: { state: SetupState | null; busy: boolean }) {
  const running = !!state?.running
  const prepare = !running && !busy && !!state?.game
  const unbound = state?.unbound ?? []
  const reason = prepareReason(state)

  return (
    <div>
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

      <Section n="03" label="tune it">
        {state?.lanUrl ? <LanQr url={state.lanUrl} /> : null}
        {running ? (
          <Control />
        ) : (
          <p className="font-serif text-xl font-light text-muted-foreground italic">
            fly first — this talks to the plugin, and the plugin only exists while
            VelociDrone is running
          </p>
        )}
      </Section>
    </div>
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
        <p className="mt-1 font-mono text-[13px] text-foreground/90">{url}</p>
      </div>
    </div>
  )
}
