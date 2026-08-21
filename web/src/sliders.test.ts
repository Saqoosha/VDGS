import { describe, expect, it } from 'vitest'
import { fromSlider, fromYSlider, kYReach, toSlider, toYSlider } from './sliders'

describe('scale slider', () => {
  it('round-trips 1', () => {
    expect(fromSlider(toSlider(1))).toBeCloseTo(1, 10)
  })

  it('uses log10 so 0.01 is -2 and 100 is 2', () => {
    expect(toSlider(0.01)).toBeCloseTo(-2, 10)
    expect(toSlider(100)).toBeCloseTo(2, 10)
  })
})

describe('height slider', () => {
  it('round-trips 0', () => {
    expect(fromYSlider(toYSlider(0))).toBeCloseTo(0, 10)
  })

  it('round-trips 5.11', () => {
    expect(fromYSlider(toYSlider(5.11))).toBeCloseTo(5.11, 6)
  })

  it('clamps -206 to the slider end', () => {
    expect(Math.abs(toYSlider(-206))).toBe(1)
    expect(fromYSlider(toYSlider(-206))).toBeCloseTo(-kYReach, 6)
  })
})
