let busy = false

export async function runExclusive(fn: () => Promise<void>): Promise<void> {
  if (busy) return
  busy = true
  try {
    await fn()
    await new Promise((r) => setTimeout(r, 250))
  } finally {
    busy = false
  }
}
