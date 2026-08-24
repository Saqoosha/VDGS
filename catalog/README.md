# The published catalog

What the companion app offers under **02 get**. One file per capture here, the sizes and
digests filled in at packaging time by `tools/make-catalog.sh`.

```
catalog/entries/<id>.json     what a capture is: name, author, licence, where it installs
catalog/tracks/<name>.track.json   the course, exported from a VelociDrone database
```

An entry is metadata only. The capture itself is hundreds of megabytes and is built by
`tools/make-release.sh --scene`; it is never committed.

## Publishing one

```bash
# 1. get the course out of the game's database (on the Windows box)
VDGS.exe --export-track "VDGS FDF" VDGS-FDF.track.json

# 2. package the capture
bash tools/make-release.sh --scene FDF-2026-08-24 --scene-dir <path> --scene-only

# 3. build the catalog and the upload set
bash tools/make-catalog.sh --base-url https://vdgs.saqoo.sh

# 4. upload build/release/site/ to the host
```

`scene_id` in a track file is the game's own scenery id, and a capture is placed relative
to that scenery's origin. **A track exported from one version of the game is not
guaranteed to line up in another** if the scenery moves; there is no way to detect that
from here, so a capture that suddenly sits wrong is worth re-checking against the track
rather than the placement.

## What may be published

Only captures whose licence allows redistribution. Most public 3DGS scenes do not: the
default is all rights reserved, and an absent licence is not permission. The reasoning and
the per-source verdicts are in [AGENTS.md](../AGENTS.md).
