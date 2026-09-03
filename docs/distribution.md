# Shipping it: the companion app (`companion-tauri/`)

*[日本語版](distribution.ja.md)*

**How the mod reaches other people.** What is inside the companion app, the run that
assembles a release and puts it up, the Cloudflare arrangement it lands on, and the
measured numbers a walkthrough can be checked against. **None of this is needed for
day-to-day work** — read it when you are shipping, or when something you shipped is
broken.

Getting a capture in at all is [SCENES.md](SCENES.md); laying a course over it is
[TRACKS.md](TRACKS.md); the user-facing steps are [USAGE.md](USAGE.md). **What may be
redistributed** is decided in [AGENTS.md](../AGENTS.md) under "splat データは配布できない。
同梱もしない" — moving a file to a different host does not make republishing it something
else.

---

**It is the tool that ships the mod, and it asks four clicks of a person.** Fetching
BepInEx, installing and removing the mod, downloading and installing a capture,
registering a track in the game's database and binding it, launching with
`-force-d3d12` — all of it happens here. **One app, Tauri 2 + Rust**, drawing the `web/`
React app (`companion.html`). Same theme, same components, same fonts as the in-game
control UI, so that it **does not read as a different product**.

```
companion-tauri/src-tauri/src/
  lib.rs       the window, dispatch, heavy work off the UI thread
  cli.rs       the two things that open no window (--export-track / --check-catalog)
  game.rs      finding and scanning the game, install/remove, unzip, writing bindings
  bepinex.rs   fetching the loader (version and digest pinned)
  tracks.rs    reading and writing user11.db
  catalog.rs   fetching, validating and downloading the published catalog
  settings.rs  remembers the game path and catalog URL (%LOCALAPPDATA% on Windows)
  launch.rs    starting the game and telling whether it is up
  state.rs     assembling the state the page is sent
```

**Windows used to be a second implementation** (`companion/`, .NET Framework 4.8 +
WinForms + WebView2). The same ten commands were written twice and kept one for one for a
while; that is over and the C# is deleted. `bridge.ts` lost its `chrome.webview`
transport at the same time.

**Empty the payload before refilling it.** A rule inherited from the C# days, for the same
reason: web assets get a fresh content hash on every build, so **each refill leaves the
previous one lying next to it** — a count on 2026-09-01 found 23 files in a payload whose
source had 5. All of them went into the zip and out to users' `vdgs/ui`. Because
`index.html` names only the two current files, **nothing broke**, and it went unnoticed
across five releases. `make-win-app.sh` and `make-mac-app.sh` now `rm -rf` `resources/mod`
before staging into it.

**The mod ships inside the app** (`resources/mod` — the tree the release scripts assemble,
copied in at build time). Buttons say what they will actually do: `INSTALL MOD` /
`REINSTALL MOD` / `UPDATE TO <version>` / `NO MOD PAYLOAD`. Nobody should have to press a
button to find out what it does.

**BepInEx is fetched rather than bundled** (and only when it is absent) — from the
upstream release, with URL, size and sha256 pinned. **"Install BepInEx first" was step one
of every install and the step most often got wrong.** Unpack it one level too deep and
**the game starts and nothing happens.**

**Tracks can be removed one row at a time** (a `REMOVE` that appears on hover). What goes
is the row in `user11.db` and the entry in `bindings.json`; **the capture stays** — it is
gigabytes, and there is no reason to delete it. **Tracks that came from the official
server are never deleted** (the button reads `UNBIND` instead): they belong to their
author, and removing them from someone's machine is not ours to do. The database is copied
before any deletion — **lap times cannot be re-obtained.**

**UNINSTALL does not delete captures either.** It removes `VDGS.dll`, `vdgs-shaders` and
`vdgs/ui`, and nothing else. Captures are gigabytes and hours to re-download, and keeping
`bindings.json` and `placement.json` means **a reinstall puts everything back where it
was**. BepInEx stays too; it is not ours.

**The game path is remembered under `%LOCALAPPDATA%`.** When it is not known, fixed disks
are scanned (depth-limited, skipping `Windows` and friends, never following junctions).
**PatchKit records the install location neither in the registry nor in its own folder**
(`%LOCALAPPDATA%\PatchKit` holds a single 32-byte `sender_id`), so scanning or asking the
user are the only options.

## Traps that cost real time

(The one about the WebView2 host holding files so a deploy lands half-new, and the one
about a `ui/` under `BaseDirectory` being unreadable over `file://`, went with the C#
app. Both were specific to the WinForms host.)

- **`scp host:relative` can exit 0 having transferred nothing.** `-v` shows
  `Executing: cp --` — **it decided this was a local copy** (a one-character hostname
  looks like a drive letter). Use an absolute path: `scp host:/C:/Users/<you>/name`
- **The body of `ssh host '...'` is parsed by PowerShell twice.** The remote default shell
  is PowerShell, and **it expands `$` before `powershell -Command "..."` ever sees it**.
  `$env:USERPROFILE` survives because the outer shell has one, but **`$_` is empty there**,
  so `ForEach-Object { $_.Name }` arrives as
  `ForEach-Object { .Name }` and dies with `.Name is not recognized`. For a one-liner,
  avoid `$_` — `Select-Object -ExpandProperty Name`. **The reliable answer is to `scp` a
  `.ps1` and run it with `-File`**, which removes one layer of quoting
- **PowerShell does not wait for a GUI-subsystem exe.** `& $exe args` returns immediately,
  so the output and the file it was going to write both "never existed". Use
  `Start-Process -Wait -PassThru`, and note that **`-ArgumentList` does not quote arguments
  containing spaces for you** — write `'"..."'` yourself
- **`Get-Process VDGS` can return an array.** With two running, `AppActivate($p.Id)` fails
  with `DISP_E_TYPEMISMATCH`. Put a `Select-Object -First 1` in the way
- **With `web/` in `.gitignore`, new files under `web/` vanish on `git add`.** A leftover
  from before the React rewrite. It came back once through a merge, and a commit nearly
  left half its own source behind
- **On macOS, `site.tsx` and `Site.tsx` are the same file.** Write both and only the later
  write survives. An entry point was deleted and **tsc, the build and the page tests all
  passed** (every test rendered components directly). Each entry is now pinned to actually
  mount (`web/src/entries.test.tsx`)
- **Building the state is expensive.** It walks every capture, reads `.ply` headers and
  opens SQLite. Sending the "working" indicator through it meant **silence until the walk
  finished** — precisely what the progress indicator existed to fix. Progress and busy go
  through their own lightweight messages. Each state carries the `stateMs` it took to
  build, so the next slowdown is measurable rather than guessed
- **Do not put a `RuntimeIdentifier` on the test project.** Mono picks up the Windows
  `e_sqlite3.dll` and crashes. Only the app itself needs a RID

## The release run

**Captures are not bundled with the mod** (hundreds of megabytes). They come from the
catalog, through the app's `02 GET`. **The four steps — export the track, pack it, build
the catalog, upload — are in catalog/README.md.** Why sizes and digests are measured from
the real file rather than typed is in docs/TRACKS.md.

The app has three defences:

- it refuses anything but https (loopback excepted)
- it does not unpack what does not match the digest
- it does not read a `formatVersion` it has never heard of

**The worst outcome is a missing field read as "no track", installing half of what was
published.**

**The mod's version is the release date, and only `make-release.sh` stamps it**
(`-p:Version=$VERSION`). The `<Version>0.1.0</Version>` in `src/VDGS/VDGS.csproj` is a
placeholder, and a build that ships it is **a dev build**. The companion's mod button
compares the installed version against the bundled one, so **while every build claims the
same 0.1.0.0 it can only ever offer "Reinstall mod".**

**The catalog carries no version requirement.** It will need one when a capture is
published that an older mod cannot draw, and it gets designed then
([#4](https://github.com/Saqoosha/VDGS/issues/4)).

**`publish.sh` uploads to R2 before it deploys.** The other order
**guarantees publishing, once, a list pointing at files that do not exist yet.**

**Uploads go through `rclone`. `wrangler` fails above 300 MiB, every time**
(`Wrangler only supports uploading files up to 300 MiB in size`, with no multipart option).
FDF 2026-08-22 is 375 MB and hit exactly this. Credentials come from the 1Password `VDGS`
environment mounted at `~/.claude/1p-mounts/vdgs.env` (override with `VDGS_R2_ENV`).

**`--s3-no-check-bucket` is required.** The token is scoped to Object Read & Write on the
`vdgs` bucket alone, and `rclone` by default confirms the bucket exists with a
`CreateBucket` that comes back 403. **The fix is on the calling side, not in widening the
token.**

**Never replace the contents of a published name.** Files are served
`immutable, max-age=31536000`, so overwriting one leaves the edge serving the old bytes for
a year while the catalog claims the new digest. **The download completes and then the
unpack is refused.** Bump the version and use a new name. This was nearly done to
`vdgs-companion-2026.09.01.zip` on 2026-09-01.

**The watch is on the digest, not on the size.** `publish.sh` stamps each object's sha256
as user metadata and compares against it next time. Three companion builds from one day
came to 6,607,301 / 6,607,540 / 6,607,546 bytes — **different contents landing on the same
length is ordinary.**

**`rclone` writes no metadata from `--metadata-set` alone; it needs `-M`.** The flag is
accepted, nothing happens, and the next run has nothing to compare against and falls back
to length. Found by measuring.

**`make-catalog.sh` picks the companion build by mtime.** Sorted by name, `2026.09.01.1`
comes before `2026.09.01` (`1` sorts before `z`), so a second build on the same day loses
to the first.

**Only publish what a licence permits you to redistribute.** The absence of a notice is
not permission. That judgment lives in "splat データは配布できない。同梱もしない".

## Hosting (`worker/`)

**https://vdgs.saqoo.sh/ — one Cloudflare Worker.**

| Path | Served from |
|---|---|
| `/`, `/assets/*`, `/catalog.json` | static assets (`build/release/site`) |
| `/scene/*`, `/track/*`, `/app/*` | R2 bucket `vdgs` (`build/release/files`) |

**They are split for size and nothing else** — a deploy caps a single file at 25 MiB, and a
capture is hundreds of megabytes. **The origin is one**, so the catalog's URLs and the
page's links cannot disagree. R2 supports Range, so downloads stream and resume across a
dropped connection, and `immutable` gives them a long cache (a published name never changes
contents).

- **`wrangler` is already authenticated as `a@saqoo.sh`**
  (`~/.wrangler/config/default.toml`), so `npx wrangler` just works. **The dashboard is not
  needed** — the custom domain is attached through `routes` with `custom_domain: true` in
  `wrangler.jsonc`
- **The saqoo.sh zone rejects `User-Agent: Python-urllib/*` with a 403.** curl gets 200 and
  so does the companion (`VDGSCompanion`). Set a UA when calling it from a script

## Numbers to check a walkthrough against

| Measured | Value |
|---|---|
| `INSTALL MOD` into an untouched game folder | 133 files / 5.2 MB, about a second |
| `vdgs-shaders` | 1,538,627 bytes (under 1 MB means a failed bake) |
| the companion zip | about 6.0 MB |
| downloading a capture | 54 s cold, 5 s once the edge has it (measured at 134 MB) |

The two scenes currently deployed, both flown on the real machine (cleanup and collision
settings are in docs/cleanup.ja.md, Japanese only):

| | splats | size | collision |
|---|---|---|---|
| `JDL-2026-R5-airvis` | 2,521,003 | 212 MB | 1,597,643 triangles / 27 MB / 0.137 m edge |
| `FDF-2026-08-22` | 4,508,391 | 362 MB | 2,197,134 triangles / 39 MB / 0.213 m edge |

`FDF-2026-08-22`'s distribution zip is 375,693,617 bytes
(sha256 `cecf661690560e42887422c794b4d81693201f188f3248b7b5d2ab18984eddb2`).

**Take that from the published catalog, not from a local zip.** Repacking the same splats
produces a file 11 bytes different, and **identical contents do not give an identical
digest**. A baseline written from a local build will always disagree with what was
published — as it once did.
`curl -A VDGSCompanion https://vdgs.saqoo.sh/catalog.json` is the source of truth.

**The GUI can only be checked on the real machine.** Session 0 has no window, so launching,
screenshotting and clicking all go through a scheduled task
(`New-ScheduledTaskPrincipal -LogonType Interactive`). `tools/appstart-win.ps1` starts the
app and leaves it running; the screenshot script brings it forward and captures.
**Synthetic clicks** (`SetCursorPos` + `mouse_event`) press the real buttons — point them
at a decoy game folder (a single empty file named `velocidrone.exe`) and **a full
walkthrough runs without touching the real install**.
