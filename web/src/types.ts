export type CollisionView = 'off' | 'solid' | 'wire'

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
  backdrop: boolean
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
}

export type CatalogState = {
  url: string
  /** Why the list is empty, when it is empty for a reason worth showing. */
  error: string | null
  entries: CatalogEntry[]
}
