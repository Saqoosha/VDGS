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

/** What the companion app knows about this machine before the game is started. */
export type SetupState = {
  game: string | null
  mod: string | null
  missing: string[]
  ready: boolean
  running: boolean
  launchArgs: string
  captures: Capture[]
}
