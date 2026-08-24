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
}

/** What the companion app knows about this machine before the game is started. */
export type SetupState = {
  game: string | null
  mod: string | null
  missing: string[]
  ready: boolean
  running: boolean
  launchArgs: string
  tracks: TrackEntry[]
  /** Installed captures no track points at - otherwise they are invisible here. */
  unbound: Capture[]
}
