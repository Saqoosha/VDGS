export type CollisionView = 'off' | 'solid' | 'wire'

/** Which of the capture's axes points at the sky. */
export type UpAxis = '+x' | '-x' | '+y' | '-y' | '+z' | '-z'

export type Scene = {
  name: string
  source: 'local' | 'catalog'
  kind: 'converted' | 'ply'
  splats: number
  posFormat?: string
  scaleFormat?: string
  colorFormat?: string
  shFormat?: string
  bytes?: number
  hasCollision: boolean
  shown: boolean
  scale: number
  y: number
  x: number
  z: number
  /** null when the placement has none yet - see placement.json's own `up` field. */
  up: UpAxis | null
  turn: number
  mirror: boolean
  backdrop: boolean
  blackout: boolean
  collision: boolean
  collisionView: CollisionView
}

export type Status = {
  track: string | null
  loaded: string[]
  available: Scene[]
  bindings: Record<string, string[]>
}

export type Capture = {
  name: string
  splats: number
  collision: boolean
  bytes?: number
}

/**
 * A track the mod will show a capture on. This is the unit the player thinks in: they
 * pick a track in VelociDrone, and the capture bound to its name appears.
 */
export type TrackEntry = {
  track: string
  capture: string | null
  /** The bound capture names one by one; `capture` is them joined for display. */
  captures?: string[]
  splats: number
  bytes?: number
  collision: boolean
  captureInstalled: boolean
  /** false: a .ply the plugin parses at load time rather than a converted directory. */
  converted: boolean
  inGame: boolean
  /** Downloaded from the official track server: it can be unbound, never deleted. */
  fromServer: boolean
}

/** What the companion app knows about this machine before the game is started. */
export type SetupState = {
  game: string | null
  mod: string | null
  /** The mod version this app carries, or null if it was built without a payload. */
  bundledMod: string | null
  missing: string[]
  ready: boolean
  running: boolean
  /** What the app is doing right now, or null. Installing takes seconds, not an instant. */
  busy: string | null
  /** How far through, when that is knowable. */
  busyPercent: number | null
  /** How long the host took to gather this. Shown only when it is slow enough to matter. */
  stateMs?: number
  launchArgs: string
  /**
   * This machine's LAN address for the plugin's HTTP server, or null when no
   * outward-facing interface was found. state.rs's `lan_url()` asks the OS for this
   * address unconditionally - it does not consult `running` - so a non-null value here
   * does NOT mean the server is actually there to reach. `running` is what says that
   * (CompanionApp gates on it alongside `lanUrl` for exactly this reason).
   */
  lanUrl: string | null
  tracks: TrackEntry[]
  catalog: CatalogState | null
  /** Installed captures no track points at - otherwise they are invisible here. */
  unbound: Capture[]
  /**
   * VelociDrone's True Lens setting. null = unknown; only true must warn — with it on
   * captures are drawn and never reach the screen, and every log still says success.
   */
  trueLens: boolean | null
}

/** One capture on offer from the published catalog. */
export type CatalogEntry = {
  id: string
  name: string
  description: string | null
  author: string | null
  licence: string | null
  splats: number
  bytes: number
  installed: boolean
  /** Installed, and the catalog has a newer cut of the same folder. */
  update: boolean
  /**
   * The capture directory this entry installs as - a different namespace from `id`. This
   * is what a track's bound capture name is matched against to find which id to hand
   * `get`; optional because older/partial fixtures do not carry it.
   */
  installAs?: string | null
  /** The track this entry installs, as displayed - what a track row claims it by. */
  track?: string | null
}

export type CatalogState = {
  url: string
  /** Why the list is empty, when it is empty for a reason worth showing. */
  error: string | null
  entries: CatalogEntry[]
}
