/**
 * Two languages for the part of the public page that explains what to do.
 *
 * Only that part. The list above it is names, numbers and licences - a Japanese reader
 * gains nothing from "4,508,391 splats" in Japanese - so the switch moves the
 * instructions and leaves the data alone.
 *
 * No i18n library. There are a dozen strings here; a framework to hold them would be
 * larger than the thing it holds.
 */
export type Lang = 'en' | 'ja'

/**
 * The names of the app's own buttons stay in English in both languages, because the app
 * itself is in English. Translating "Install mod" here would tell a reader to look for a
 * button that does not exist under that name.
 */
export const how = {
  en: {
    label: 'how',
    step1: 'Download the companion below and run it.',
    step2a: 'Press ',
    step2b:
      '. It looks for VelociDrone in the usual places, fetches BepInEx if you do not have ' +
      'it, and puts the mod in place. VelociDrone has no installer - it lives wherever its ' +
      'zip was unpacked - so if it is somewhere unusual the guess misses and you point at ' +
      'the folder yourself with Change.',
    step3a: 'Open ',
    step3b: ' and take a scan. It downloads the scene, adds the track and binds the two.',
    step4a: 'Press ',
    step4b: '.',
    download: 'Download the companion',
    d3d12:
      'the game must be started with -force-d3d12, which the companion always does. ' +
      'without it the scans do not draw at all, and nothing says why.',
    loaderA: 'the loader is ',
    loaderB: ', fetched from its own release and checked against a pinned digest.',
  },
  ja: {
    label: '使い方',
    step1: '下から companion を落として起動する。',
    step2a: '',
    step2b:
      ' を押す。companion が VelociDrone をよくある場所から探し、BepInEx が無ければ取ってきて、' +
      'mod を置く。VelociDrone にはインストーラが無く、zip を解凍した場所がそのまま置き場所に' +
      'なるので、変わった場所にあると見つからない。そのときは Change で自分でフォルダを指す。',
    step3a: '',
    step3b: ' からスキャンを取る。シーンを落とし、コースを追加し、両者を結びつける。',
    step4a: '',
    step4b: ' を押す。',
    download: 'companion をダウンロード',
    d3d12:
      'ゲームは -force-d3d12 で起動する必要がある。companion は必ずそうする。' +
      '付けないとスキャンは一切描かれず、理由も表示されない。',
    loaderA: 'ローダーは ',
    loaderB: '。本家のリリースから取得し、固定した digest と照合する。',
  },
} as const satisfies Record<Lang, Record<string, string>>

const KEY = 'vdgs.lang'

/**
 * What to show before anyone has chosen: a remembered choice, else the browser's own
 * language, else English. Reading localStorage can throw outright in a private window or
 * with site data blocked, so a failure here falls back rather than taking the page down.
 */
export function initialLang(): Lang {
  try {
    const saved = localStorage.getItem(KEY)
    if (saved === 'en' || saved === 'ja') return saved
  } catch {
    /* no stored preference is reachable; fall through to the browser's */
  }
  const nav = typeof navigator === 'undefined' ? '' : navigator.language
  return nav.toLowerCase().startsWith('ja') ? 'ja' : 'en'
}

export function rememberLang(lang: Lang): void {
  try {
    localStorage.setItem(KEY, lang)
  } catch {
    /* the choice still applies to this page; it just will not outlive it */
  }
}
