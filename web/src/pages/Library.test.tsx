import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import Library from './Library'
import { StatusContext } from '../status-context'
import type { Scene, Status } from '../types'

const xss = '<img src=x onerror=alert(1)>'

function scene(over: Partial<Scene> = {}): Scene {
  return {
    name: 'playroom',
    source: 'local',
    kind: 'converted',
    splats: 10,
    hasCollision: false,
    shown: false,
    scale: 1,
    y: 0,
    backdrop: false,
    collision: false,
    collisionView: 'off',
    ...over,
  }
}

function renderLib(status: Status) {
  return render(
    <StatusContext.Provider value={{ state: status, live: true, refresh: async () => {} }}>
      <MemoryRouter>
        <Library />
      </MemoryRouter>
    </StatusContext.Provider>,
  )
}

describe('Library XSS', () => {
  it('renders a hostile scene name as text, not HTML', () => {
    renderLib({
      track: null,
      loaded: [],
      available: [scene({ name: xss })],
      bindings: {},
    })
    expect(screen.queryByRole('img')).toBeNull()
    expect(screen.getByText(xss)).toBeInTheDocument()
  })
})
