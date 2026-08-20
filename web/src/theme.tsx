import { useEffect, useState } from 'react'

export type Theme = 'survey' | 'particles'

const KEY = 'vdgs-theme'

export function useTheme() {
  const [theme, setTheme] = useState<Theme>(() => {
    try {
      const saved = localStorage.getItem(KEY)
      if (saved === 'survey' || saved === 'particles') return saved
    } catch {
      /* ignore */
    }
    return 'particles'
  })

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    try {
      localStorage.setItem(KEY, theme)
    } catch {
      /* ignore */
    }
  }, [theme])

  return [theme, setTheme] as const
}
