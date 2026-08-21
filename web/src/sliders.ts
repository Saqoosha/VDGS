export const kYReach = 200

export function toSlider(v: number): number {
  return Math.log10(Math.max(0.01, v))
}

export function fromSlider(t: number): number {
  return Math.pow(10, t)
}

export function toYSlider(v: number): number {
  const c = Math.max(-kYReach, Math.min(kYReach, v))
  return Math.sign(c) * Math.log1p(Math.abs(c)) / Math.log1p(kYReach)
}

export function fromYSlider(t: number): number {
  return Math.sign(t) * Math.expm1(Math.abs(t) * Math.log1p(kYReach))
}
