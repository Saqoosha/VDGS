# Building a track

*[日本語版](TRACKS.ja.md)*

Laying your own course over a capture, and getting it into a shape someone else can fly.
Getting a capture in at all is [SCENES.md](SCENES.md); installing and driving the mod is
[USAGE.md](USAGE.md). **This file is the part between them** — from "the picture is there"
to "this is a track people can download".

VelociDrone's track editor itself is not documented here; it is the game's own. What
follows is only the part VDGS touches.

---

## The order is the whole thing

**Name it, bind it, then build it.** Any other order costs you work.

**What shows is decided by the track's name alone** (`bindings.json`). So:

- Renaming a track after building it **breaks the binding and the picture disappears**.
  Re-binding fixes it, but a released `bindings.sample.json` assumes the name it shipped
  with — so **if you intend to publish, keep the name you started with**
- Bind before you open the editor and **the capture is there the whole time you are
  building**

## 1. Pick a scenery

A track sits on one of VelociDrone's sceneries (`scene_id`), and **a capture is placed
relative to that scenery's origin** — so `scene_id` is also the number that decides whether
a published track lands where its capture is.

**Pick a flat, empty one.** The game's own terrain and buildings otherwise compete with the
capture; `SplatBackdrop` boxes the capture in black precisely to keep the outside world out
of the picture.

What each number is on your install:

```powershell
VDGS.exe --export-track --list
# [local]  scene  16  VDGS FDF
# [server] scene  33  ...
```

**The same works on macOS** — it moved across when the two companions became one; the C#
version was Windows-only. Call the binary inside the bundle:

```bash
"/Applications/VDGS Companion.app/Contents/MacOS/VDGS Companion" --export-track --list
```

**All three published VDGS tracks sit on `scene 16`.** Whether that number is the same
across installs is not verified here, so **read your own list rather than trusting the
number**.

## 2. Bind first

Put the capture down and bind it to the track's name. The browser UI
(`http://localhost:8777/`) is quickest: `01 CONTROL` shows the current track, so
**Bind shown splat to this track** is one press.

By hand, `<game>/vdgs/bindings.json`:

```json
{ "My Track": ["my-capture"] }
```

**An unbound track shows nothing**, which is less harmful than showing the wrong capture.

To fly a track without its capture, start the game without `-force-d3d12` - VelociDrone's
own launcher does not pass it. The splat shaders bake as unsupported without D3D12, so no
capture is read at all.

## 3. Get the placement right

**Scale** and **Height** live in `02 LIBRARY`. **Changes are saved to `placement.json` as
you make them**, so once it looks right you can go straight to building.

**Placement belongs to the capture, not to the course.** It is relative to the scenery's
origin, so another course on the same scenery inherits it. The other way round: a
**`placement.json` that came with a download is tuned to the track it shipped with**, so
adjust it here if you are laying your own course.

The sliders are logarithmic. **Height reaches ±200 m** — some captures have their origin
200 m underground. Type an exact value into the box beside it when you need one.

## 4. Build

Build in the game's editor as usual. **The mod takes no keys at all** — F7 (save scene) and
the arrow keys (move object) stay the game's. Everything is driven from the browser.

Three things matter from the VDGS side:

- **The game must be running with `-force-d3d12`.** Without it no capture draws at all and
  nothing says why. The companion's `FLY` always passes it
- **Do not linger in the menus.** Left on the main menu under D3D12 the **game crashes**
  after about five minutes. It crashes with the plugin removed too, so the mod is not
  involved — see [AGENTS.md](../AGENTS.md). Inside the editor or a track it does not happen
- **Swapping what is shown stalls a frame** (tens of megabytes go to the GPU). A bare `.ply`
  is **re-parsed every time**: 13–14 seconds at four million splats. **Convert before you
  build** and that wait all but disappears ([USAGE.md](USAGE.md) §4-2)

## 5. Bake collision

**Without it you fly through the walls and the floor.** The bake is [SCENES.md](SCENES.md)
§4.

**Wall thickness is decided by speed.** Physics runs at 400 Hz, so at 150 km/h one step
covers 0.104 m and **any wall thinner than 10 cm is passed through**. That is why the level
set band is baked at four times the voxel size.

**show solid** in the browser UI draws the shell, so you can see whether the walls read
from the inside before committing.

## 6. Export it

```powershell
VDGS.exe --export-track "My Track" My-Track.track.json
```

**Tracks downloaded from the official track server are refused.** Their author put them
there; they are not ours to hand out under our own catalog.

What comes out is a small JSON of four fields — `name`, `scene_id`, `type`, `value`. The
`value` is the game's own string **byte for byte**, unformatted, so an imported track does
not differ from the original in any way. FDF's is 3,772 bytes.

## 7. Publish it

Write `catalog/entries/<id>.json`, package, upload — all in
[catalog/README.md](../catalog/README.md).

**Sizes and digests are measured, never typed.** A digest is the only thing standing
between a truncated or swapped download and files unpacked over someone's game folder, and
`tools/make-catalog.sh` reads them off the real file.

**Settle whether the capture may be redistributed before any of this.** An absent licence
is not permission; the per-source verdicts are in [AGENTS.md](../AGENTS.md).

---

## Where it goes wrong

| Symptom | Cause |
|---|---|
| In the editor, no picture | No binding for that track name — or you renamed it |
| In the editor, the **wrong** capture | The track-name lookup fell through to the flight HUD label, which still holds the last track flown. Reload the track |
| Nothing draws, ever | Not launched with `-force-d3d12`; or `vdgs-shaders` is under 1 MB and needs re-baking |
| You fly through walls | No `collision.bin`, or the walls are thinner than 10 cm |
| Published, and it sits in the wrong place | `placement.json` was not included, or the person who downloaded it renamed the track |
| The game dies after ~5 minutes | Left sitting on the main menu; nothing to do with the mod |
