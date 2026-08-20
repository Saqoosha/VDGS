import { useCallback, useEffect, useState } from 'react'
import { getStatus } from './api'
import type { Status } from './types'

export function useStatus() {
  const [state, setState] = useState<Status | null>(null)
  const [live, setLive] = useState(false)

  const refresh = useCallback(async () => {
    try {
      const next = await getStatus()
      setState(next)
      setLive(true)
    } catch {
      setLive(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
    const id = window.setInterval(() => {
      void refresh()
    }, 1500)
    return () => window.clearInterval(id)
  }, [refresh])

  return { state, live, refresh }
}
