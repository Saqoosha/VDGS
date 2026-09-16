# Building a track

*[日本語版](TRACKS.ja.md)*

Laying your own course over a capture, and getting it into a shape someone else can fly.
Getting a capture in at all is [SCENES.md](SCENES.md); installing and driving the mod is
[USAGE.md](USAGE.md). **This file is the part between them** — from "the picture is there"
to "this is a track people can download".

VelociDrone's track editor itself is not documented here; it is the game's own. What
follows is only the part VDGS touches.

---

## What the companion handles: naming, binding, placement

**Starting a new track from your own capture is mostly done with the companion's
`Add track`.** Pick a `.ply`, type a name and Create — that copies the file into
`<game>/vdgs/`, creates the track and binds it in one job — then Fly and tune the
placement from **Tweak**. Binding lands in the same job that creates the track, so
**the old ordering trap — build first, rename later, watch the picture
disappear — cannot happen for a track made this way** (a job that fails partway leaves
the copied capture listed as installed, on no track). The walkthrough is in
[USAGE.md](USAGE.md).

**There is no scenery to pick.** The seed template the companion clones already carries a
`scene_id`, and its gates sit where they do because of that one scenery — there is no room
to choose a different one. Looking scenery numbers up with `--export-track --list` is gone.

What is left in this file are the four steps the companion does not reach yet — **from
here on it is the game's own track editor and this repo's tools.**

## Renaming a track breaks its binding

**What shows is decided by the track's name alone** (`bindings.json`). For a track the
companion just created, the name and the binding land in the same job — but **rename the
track afterward and the binding breaks, and the picture disappears.** Re-binding fixes it,
but a released `bindings.sample.json` assumes the name it shipped with, so **if you intend
to publish, do not rename it partway through.**

## 1. Build

Build in the game's editor as usual. **The mod takes no keys at all** — F7 (save scene) and
the arrow keys (move object) stay the game's. Placement is tuned from the
**Tweak** screen.

Two things matter from the VDGS side:

- **The game must be running with `-force-d3d12`.** Without it no capture draws at all and
  nothing says why. The companion's `FLY` always passes it
- **Do not linger in the menus.** Left on the main menu under D3D12 the **game crashes**
  after about five minutes. It crashes with the plugin removed too, so the mod is not
  involved — see [AGENTS.md](../AGENTS.md). Inside the editor or a track it does not happen

**Swapping what is shown stalls a frame** (tens of megabytes go to the GPU). A bare `.ply`
is **re-parsed every time**: 13–14 seconds at four million splats.
**Convert before you build** and that wait all but disappears ([USAGE.md](USAGE.md) §4-2).

## 2. Bake collision

**Without it you fly through the walls and the floor.** The bake is [SCENES.md](SCENES.md)
§4.

**Wall thickness is decided by speed.** Physics runs at 400 Hz, so at 150 km/h one step
covers 0.104 m and **any wall thinner than 10 cm is passed through**. That is why the level
set band is baked at four times the voxel size.

**Collision view** on the **Tweak** screen draws the shell, so you can see
whether the walls read from the inside before committing.

## 3. Export

```powershell
VDGS.exe --export-track "My Track" My-Track.track.json
```

**Tracks downloaded from the official track server are refused.** Their author put them
there; they are not ours to hand out under our own catalog.

What comes out is a small JSON of four fields — `name`, `scene_id`, `type`, `value`. The
`value` is the game's own string **byte for byte**, unformatted, so an imported track does
not differ from the original in any way. FDF's is 3,772 bytes.

## 4. Publish

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
