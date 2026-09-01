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
    // Unzip is named because the download is a zip: "download and run it" is an
    // instruction nobody can follow.
    step1a: 'Download the companion, unzip it, and run ',
    step1b: '.',
    // What to do when the button will not press, rather than why it will not.
    step2a: 'Press ',
    step2b: '. If it is greyed out, use ',
    step2c: ' to point at your VelociDrone folder.',
    // The size is the reason to wait; how it installs is not the reader's problem.
    step3a: 'Download the scan and its track from ',
    step3b: '. A few hundred megabytes, so give it a moment.',
    // Without this last half nothing appears and nothing says why: a scan is shown on
    // the track it is bound to, so any other track is an empty sky.
    step4a: 'Press ',
    step4b: ', then pick that track by name in VelociDrone.',
    download: 'Download the companion',
    d3d12:
      'the game must be started with -force-d3d12, which the companion always does. ' +
      'without it the scans do not draw at all, and nothing says why.',
    loaderA: 'the loader is ',
    loaderB: ', fetched from its own release and checked against a pinned digest.',
  },
  ja: {
    label: '使い方',
    step1a: 'companion をダウンロードして解凍し、',
    step1b: ' を起動する。',
    step2a: '',
    step2b: ' を押す。押せないときは ',
    step2c: ' で VelociDrone のフォルダを指す。',
    step3a: '',
    step3b: ' からスキャンとトラックデータをダウンロードする。数百 MB あるのでしばし待つ。',
    step4a: '',
    step4b: ' を押し、VelociDrone でそのコースを名前で選ぶ。',
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
