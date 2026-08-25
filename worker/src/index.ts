/**
 * vdgs.saqoo.sh — the page, the catalog, and the captures themselves.
 *
 * The page and catalog.json are static assets. The captures are not: a capture is
 * hundreds of megabytes, which is far past what a static deployment will take, so they
 * live in R2 and are streamed through here. One origin for all of it, so a catalog entry
 * and the page it is listed on can never disagree about where a file is.
 */
export interface Env {
  ASSETS: Fetcher
  CAPTURES: R2Bucket
}

// Everything else is the site.
const FROM_R2 = /^\/(scene|track)\//

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url)
    if (!FROM_R2.test(url.pathname)) return env.ASSETS.fetch(request)

    const key = decodeURIComponent(url.pathname.slice(1))
    if (request.method !== 'GET' && request.method !== 'HEAD')
      return new Response('method not allowed', { status: 405, headers: { Allow: 'GET, HEAD' } })

    // Range matters here: these are big files over links that drop, and a browser that
    // cannot resume starts a 120 MB download again from zero.
    const object = await env.CAPTURES.get(key, {
      onlyIf: request.headers,
      range: request.headers,
    })
    if (object === null) return new Response('not found', { status: 404 })

    const headers = new Headers()
    object.writeHttpMetadata(headers)
    headers.set('etag', object.httpEtag)
    headers.set('accept-ranges', 'bytes')
    // Published files never change under the same name - a new capture gets a new one -
    // so a long cache costs nothing and saves the whole download on a second machine.
    headers.set('cache-control', 'public, max-age=31536000, immutable')

    if (!('body' in object)) return new Response(null, { status: 304, headers })

    if (object.range && 'offset' in object.range) {
      const start = object.range.offset ?? 0
      const length = object.range.length ?? object.size - start
      headers.set('content-range', `bytes ${start}-${start + length - 1}/${object.size}`)
      return new Response(object.body, { status: 206, headers })
    }
    return new Response(object.body, { headers })
  },
}
