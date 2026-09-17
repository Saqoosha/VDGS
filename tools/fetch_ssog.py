#!/usr/bin/env python3
"""Download a SuperSplat streamed SOG into a directory the plugin can load.

    python3 tools/fetch_ssog.py 7a7bfaea build/splats/7a7bfaea
    python3 tools/fetch_ssog.py https://superspl.at/scene/7a7bfaea out/

Resolves the scene id to its lod-meta.json through the viewer page, then fetches every
chunk's meta.json and the images each one names. Re-running skips files already present
with the size the server reports, so an interrupted download resumes.

Redistribution depends on the scene's licence (see CLAUDE.md, "splat データは配布できない").
This only fetches for local use.
"""
import concurrent.futures, json, os, re, sys, urllib.request

def get(url, method='GET', attempts=4):
    # CloudFront drops the odd connection mid-read on a 250-file run; one stall should
    # not throw away the rest of the download.
    for i in range(attempts):
        try:
            req = urllib.request.Request(url, method=method)
            with urllib.request.urlopen(req, timeout=60) as r:
                return r if method == 'HEAD' else r.read()
        except (TimeoutError, OSError) as e:
            if i == attempts - 1:
                raise
            print(f'retry {i + 1}: {url}: {e}', file=sys.stderr)

def resolve(arg):
    sid = re.search(r'([0-9a-f]{8})', arg).group(1)
    html = get(f'https://superspl.at/s?id={sid}').decode('utf-8', 'replace')
    m = re.search(r'https://[a-z0-9]+\.cloudfront\.net/[^"\s]*lod-meta\.json', html)
    if not m:
        sys.exit(f'{sid}: no lod-meta.json on the viewer page (not a streamed SOG?)')
    return m.group(0)

def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    url = resolve(sys.argv[1])
    base = url.rsplit('/', 1)[0]
    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    manifest = get(url)
    open(os.path.join(out, 'lod-meta.json'), 'wb').write(manifest)
    meta = json.loads(manifest)
    metas = list(meta['filenames']) + ([meta['environment']] if meta.get('environment') else [])
    jobs = []
    for rel in metas:
        body = get(f'{base}/{rel}')
        path = os.path.join(out, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        open(path, 'wb').write(body)
        m = json.loads(body)
        folder = rel.rsplit('/', 1)[0]
        for key in ('means', 'scales', 'quats', 'sh0', 'shN'):
            for f in m.get(key, {}).get('files', []):
                jobs.append(f'{folder}/{f}')
    def fetch(rel):
        path = os.path.join(out, rel)
        size = int(get(f'{base}/{rel}', method='HEAD').headers['Content-Length'])
        if os.path.exists(path) and os.path.getsize(path) == size:
            return 0
        data = get(f'{base}/{rel}')
        if len(data) != size:
            raise RuntimeError(f'{rel}: got {len(data)} bytes, expected {size}')
        open(path, 'wb').write(data)
        return size
    with concurrent.futures.ThreadPoolExecutor(8) as ex:
        total = sum(ex.map(fetch, jobs))
    print(f'{len(metas)} chunks, {len(jobs)} images, {total / 1e6:.1f} MB fetched -> {out}')
    print(f'splats {meta["count"]:,} levels {meta["counts"]}')

if __name__ == '__main__':
    main()
