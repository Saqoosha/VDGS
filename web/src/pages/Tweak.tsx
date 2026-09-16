import { useEffect, useState } from 'react'
import QRCode from 'qrcode'
import { Button } from '@/components/ui/button'
import { useStatus } from '../useStatus'
import Control from './Control'

/**
 * The tuning screen: what a track's capture looks like in the game, adjusted while it is
 * on screen. It replaces the track list rather than opening beside it - a second window
 * is more Tauri plumbing for nothing, and anyone who wants a second screen has the LAN
 * address below, which is a phone.
 *
 * Everything here goes over the plugin's own HTTP server, so it only works while the
 * game is running with a track loaded. The list is what decides whether to offer the
 * way in (`live` and the loaded track's name); this screen just says so if it finds the
 * plugin gone, since the game can be quit while it is open.
 *
 * Opened in a plain browser - the page the plugin serves at :8777 - this is the whole
 * app, and `onBack` is absent: there is no list to go back to.
 */
export default function Tweak({
  track,
  onBack,
  lanUrl,
}: {
  /** The track this was opened for, for the heading. Null in the browser. */
  track: string | null
  onBack?: () => void
  /** Where a second screen can reach the same controls, or null when unknown. */
  lanUrl: string | null
}) {
  const { state, live, refresh } = useStatus()
  // The game can change track while this is open. The controls follow whatever the
  // plugin has loaded, so with the heading naming another track they would tune the
  // wrong capture; the table is where the right row is.
  const moved = !!track && !!state?.track && state.track !== track
  // A plugin older than this app answers without the orientation fields, and the dials
  // below would crash on them. Naming the mismatch beats a blank window.
  const older = (state?.available ?? []).some((s) => typeof s.turn !== 'number')

  return (
    <div>
      {onBack || track ? (
        <div className="mb-5 flex flex-wrap items-baseline justify-between gap-3">
          {onBack ? (
            <Button variant="ghost" size="sm" onClick={onBack} className="-ml-2">
              ← tracks
            </Button>
          ) : null}
          {track ? (
            <p className="font-serif text-[1.65rem] leading-tight font-light">{track}</p>
          ) : null}
        </div>
      ) : null}
      {lanUrl && live ? <LanQr url={lanUrl} /> : null}
      {live && older ? (
        <p className="font-serif text-xl font-light text-muted-foreground italic">
          the installed mod is older than this app — quit the game and press Update mod
        </p>
      ) : live && moved ? (
        <p className="font-serif text-xl font-light text-muted-foreground italic">
          the game moved to “{state?.track}” — go back to tracks and open it from there
        </p>
      ) : live ? (
        <Control state={state} refresh={refresh} />
      ) : (
        <p className="font-serif text-xl font-light text-muted-foreground italic">
          {onBack
            ? 'the plugin is not answering — fly this track, then come back'
            : 'waiting for the plugin…'}
        </p>
      )}
    </div>
  )
}

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
