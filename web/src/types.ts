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
