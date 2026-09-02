import { afterEach, describe, expect, it, vi } from 'vitest'
import { how, initialLang, rememberLang, type Lang } from './i18n'

function withNavigatorLanguage(value: string) {
  vi.spyOn(navigator, 'language', 'get').mockReturnValue(value)
}

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.clear()
})

describe('which language to show', () => {
  it('uses a remembered choice above everything else', () => {
    withNavigatorLanguage('ja-JP')
    rememberLang('en')
    expect(initialLang()).toBe('en')
  })

  it('follows the browser when nothing has been chosen', () => {
    withNavigatorLanguage('ja-JP')
    expect(initialLang()).toBe('ja')
  })

  it('matches on the language, not the whole tag', () => {
    withNavigatorLanguage('ja')
    expect(initialLang()).toBe('ja')
  })

  it('falls back to English for anything else', () => {
    withNavigatorLanguage('de-DE')
    expect(initialLang()).toBe('en')
  })

  // A private window can throw on the very first read rather than returning null, and a
  // page that cannot decide a language is a page that does not render.
  it('survives a localStorage that throws', () => {
    withNavigatorLanguage('ja-JP')
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied')
    })
    expect(initialLang()).toBe('ja')
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied')
    })
    expect(() => rememberLang('en')).not.toThrow()
  })
})

describe('the dictionary', () => {
  // One language gaining a string the other does not is invisible until a reader hits it,
  // so it is checked rather than watched for.
  it('has the same keys in both languages', () => {
    expect(Object.keys(how.ja).sort()).toEqual(Object.keys(how.en).sort())
  })

  it('leaves nothing empty in either language', () => {
    for (const lang of ['en', 'ja'] as Lang[])
      for (const [key, value] of Object.entries(how[lang]))
        // The lead-ins around a button name are deliberately empty in Japanese, where the
        // particle follows the name instead of preceding it. English has no such case for
        // the website steps - but the companion True Lens warning starts with the name, so
        // its English lead-in is empty the same way. Exempting other English keys would
        // let "Press Install mod" ship as "Install mod".
        if (
          !(lang === 'ja' && /^step[2-4]a$/.test(key)) &&
          !(lang === 'en' && key === 'setupTrueLensA')
        )
          expect(value, `${lang}.${key}`).not.toBe('')
  })
})
