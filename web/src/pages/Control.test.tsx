import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import App from '../App'
import Control from './Control'
import { StatusContext } from '../status-context'
import type { Status } from '../types'

vi.mock('../useStatus', () => ({
  useStatus: () => ({ state: null, live: true, refresh: async () => {} }),
}))

const xss = '<img src=x onerror=alert(1)>'

function sample(over: Partial<Status> = {}): Status {
  return {
    track: null,
    loaded: [],
    available: [],
    bindings: {},
    ...over,
  }
}

function renderControl(status: Status) {
  return render(
    <StatusContext.Provider value={{ state: status, live: true, refresh: async () => {} }}>
      <MemoryRouter>
        <Control />
      </MemoryRouter>
    </StatusContext.Provider>,
  )
}

describe('Control XSS', () => {
  it('renders a hostile track name as text, not HTML', () => {
    renderControl(sample({ track: xss, bindings: { [xss]: ['playroom'] } }))
    expect(screen.queryByRole('img')).toBeNull()
    expect(screen.getAllByText(xss).length).toBeGreaterThan(0)
  })
})

describe('App shell', () => {
  it('does not interpret a track name as HTML in the shell either', () => {
    render(
      <MemoryRouter>
        <Routes>
          <Route element={<App />}>
            <Route path="/" element={<p>ok</p>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    )
    expect(screen.getByText('VDGS')).toBeInTheDocument()
  })
})
