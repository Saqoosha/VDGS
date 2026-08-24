import { createContext, useContext } from 'react'
import type { Status } from './types'

type Ctx = {
  state: Status | null
  live: boolean
  refresh: () => Promise<void>
}

export const StatusContext = createContext<Ctx | null>(null)

export function useStatusContext(): Ctx {
  const ctx = useContext(StatusContext)
  if (!ctx) throw new Error('StatusContext missing')
  return ctx
}
