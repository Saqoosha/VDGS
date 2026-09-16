import { useCallback, useEffect, useState } from 'react'
import { getStatus } from './api'
import type { Status } from './types'

/**
 * Polls the plugin's `/api/status`. `enabled` false stops the poll and reports not live -
 * the table passes the host's `running` flag, so a closed game is not asked every 1.5 s.
 */
export function useStatus(enabled = true) {
  const [state, setState] = useState<Status | null>(null)
  const [live, setLive] = useState(false)

  // Returns what it fetched (or null on failure) rather than just resolving void: a
  // caller that just changed something on the server - the backdrop checkbox, notably -
  // needs the fresh value in hand to tell a real change from a silent refusal, and
  // `state` from this closure would still be the old render's value at that point.
  const refresh = useCallback(async (): Promise<Status | null> => {
    try {
      const next = await getStatus()
      setState(next)
      setLive(true)
      return next
    } catch {
      setLive(false)
      return null
    }
  }, [])

  useEffect(() => {
    if (!enabled) {
      setLive(false)
      return
    }
    void refresh()
    const id = window.setInterval(() => {
      void refresh()
    }, 1500)
    return () => window.clearInterval(id)
  }, [refresh, enabled])

  return { state, live, refresh }
}
