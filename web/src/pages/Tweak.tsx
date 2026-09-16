import { useStatus } from '../useStatus'
import Control from './Control'

/**
 * The tuning screen: what a track's capture looks like in the game, adjusted while it is
 * on screen. It replaces the track list rather than opening beside it - a second window
 * is more Tauri plumbing for nothing, and anyone who wants a second screen has the LAN
 * address below, which is a phone.
 *
 * Everything here goes over the plugin's own HTTP server, so it only works while the
 * game is running with a track loaded. The list decides whether to offer the way in;
 * this screen says so if it finds the plugin gone, since the game can be quit while it
 * is open. The way back is the shell's footer button, where Fly normally sits.
 *
 * Opened in a plain browser - the page the plugin serves at :8777 - this is the whole
 * app, and `onBack` is absent: there is no list to go back to.
 */
export default function Tweak({
  track,
  onBack,
}: {
  /** The track this was opened for, for the heading. Null in the browser. */
  track: string | null
  /** Present in the companion; its absence is what marks the browser build. */
  onBack?: () => void
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
      {track ? (
        <p className="mb-5 font-serif text-[1.65rem] leading-tight font-light">{track}</p>
      ) : null}
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
