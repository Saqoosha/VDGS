# vdgs.saqoo.sh

One Worker serving three things from one origin:

| Path | Where it comes from |
|---|---|
| `/`, `/assets/*`, `/catalog.json` | static assets, out of `build/release/site` |
| `/scene/*.zip`, `/track/*.json`, `/app/*.zip` | the `vdgs` R2 bucket |

The split is by size, not by kind. A capture is hundreds of megabytes — far past what a
static deployment takes — so it lives in R2 and is streamed through the Worker. Keeping
one origin means a catalog entry and the page listing it can never disagree about where a
file is.

```bash
bash tools/make-catalog.sh --base-url https://vdgs.saqoo.sh   # builds build/release/site
bash tools/publish.sh                                          # uploads captures, deploys
```

Egress from R2 is free, which is the reason the captures are there rather than anywhere
that bills per gigabyte.
