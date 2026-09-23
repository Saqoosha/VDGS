#!/usr/bin/env python3
"""Turn a VelociDrone flight recorded by tools/vd_record.py into SuperSplat camera keys.

The WebSocket reports the drone in the game's world frame. A capture sits in that world
as world = position + scale * R_y(turn) * local, with a .ply read under mirrorY also
flipped in Y; inverting that takes the flight into the frame the capture's file was
written in. --to-web then negates Z, which is how the web/SuperSplat copy of a capture
differs from the Unity one (docs/sky.ja.md).

The camera is the drone's position, looking at where the drone will be a moment later
(--lookahead). That is not the pilot's view - the FPV camera rolls and pitches with
the frame, and SuperSplat's camera has no roll to give it - but a camera that looks
along the path it is about to fly reads as a follow shot rather than as shake.

Laps are cut at the start gate, taken as where the drone was when the race reported
gate 1. --start cuts a loop from any moment to the drone's next return there instead.
A window the stream went silent in is refused rather than bridged with a straight line;
recordings made before vd_record.py stopped using the library keepalive have such gaps.
Distances (--gate-radius, the loop and hover thresholds) are in capture units.

    python3 tools/vd_path_to_supersplat.py flight.jsonl placement.json out.json
        [--lap 2 | --all | --start S] [--keys-per-second 10] [--frame-rate 30] [--to-web]
"""
import argparse
import json
import math
import os

import numpy as np


def load(path):
    t, pos, first_gate = [], [], None
    for line in open(path):
        r = json.loads(line)
        m = r["msg"]
        if not m.startswith("{"):
            continue                      # the game interleaves a few broken frames
        d = json.loads(m)
        if "imu" in d:
            i = d["imu"]
            t.append(r["t"])
            pos.append([float(i["PositionX"]), float(i["PositionY"]), float(i["PositionZ"])])
        elif "racedata" in d and first_gate is None:
            v = next(iter(d["racedata"].values()))
            if str(v.get("gate")) == "1":
                first_gate = r["t"]
    return np.array(t), np.array(pos), first_gate


def world_to_capture(pos, placement, is_ply):
    """Inverse of the mod's placement (SplatScene.Spawn): position, a rotation about Y, and for a
    .ply read with mirrorY the Y flip the loader applied (SplatScene.MirrorFor)."""
    up = placement.get("up")
    if up:                                                   # Spawn composes up and turn...
        if up != "+y":
            raise SystemExit(f"up = {up} is not handled, only +y")
        deg = float(placement.get("turn", 0.0))
    else:                                                    # ...and falls back to the raw rotation
        rx, ry, rz = (float(v) for v in placement.get("rotation", [0, 0, 0]))
        if abs(rx) > 1e-6 or abs(rz) > 1e-6:
            raise SystemExit(f"rotation {[rx, ry, rz]} is not a turn about Y, which is all that is handled")
        deg = ry
    p = np.array(placement["position"], float)
    s = float(placement.get("scale", 1.0))
    a = math.radians(deg)
    c, sn = math.cos(a), math.sin(a)
    R = np.array([[c, 0, sn], [0, 1, 0], [-sn, 0, c]])      # Unity's rotation about +Y
    local = ((pos - p) @ R) / s                              # R^T (pos - p) / s, row vectors
    mirror = placement.get("mirrorY")
    if is_ply and (mirror is None or mirror):
        local = local * np.array([1.0, -1.0, 1.0])
    return local


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("flight")
    ap.add_argument("placement")
    ap.add_argument("out")
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--lap", type=int, default=2, help="which lap, from 1")
    g.add_argument("--all", action="store_true", help="the whole race, start gate to finish")
    ap.add_argument("--keys-per-second", type=float, default=10.0)
    ap.add_argument("--frame-rate", type=int, default=30)
    ap.add_argument("--lookahead", type=float, default=0.5, help="seconds ahead the camera looks")
    ap.add_argument("--smooth", type=float, default=0.15, help="position smoothing, seconds")
    ap.add_argument("--fov", type=float, default=90.0)
    ap.add_argument("--gate-radius", type=float, default=4.0,
                    help="capture units from the start gate that count as passing it")
    ap.add_argument("--to-web", action="store_true", help="negate Z for the web/SuperSplat frame")
    ap.add_argument("--start", type=float,
                    help="loop from this many seconds after the first sample to the next return here")
    ap.add_argument("--max-gap", type=float, default=0.5,
                    help="refuse a window where the stream went silent for longer than this")
    a = ap.parse_args()

    t, world, first_gate = load(a.flight)
    if first_gate is None and a.start is None:
        raise SystemExit("no first-gate event in the flight: was a race started? (or use --start)")
    silences = [(t[i] - t[0], t[i + 1] - t[0]) for i in np.nonzero(np.diff(t) > a.max_gap)[0]]
    for s0, s1 in silences:
        print(f"stream silent {s0:6.1f} -> {s1:6.1f} s ({s1 - s0:.1f} s)")
    # A converted folder keeps placement.json inside it; a .ply keeps <name>.placement.json
    # beside it, and only a .ply is mirrored as it loads.
    is_ply = os.path.basename(a.placement) != "placement.json"
    local = world_to_capture(world, json.load(open(a.placement)), is_ply)
    if a.to_web:
        local = local * np.array([1.0, 1.0, -1.0])

    # Resample receive time onto a steady grid; the box filter below absorbs its jitter.
    rate = 60.0
    tt = np.arange(t[0], t[-1], 1 / rate)
    P = np.stack([np.interp(tt, t, local[:, k]) for k in range(3)], 1)
    k = max(1, int(round(a.smooth * rate)))
    ker = np.ones(2 * k + 1) / (2 * k + 1)
    P = np.stack([np.convolve(np.pad(P[:, i], k, mode="edge"), ker, "valid") for i in range(3)], 1)

    def silent_between(s, e):
        return [x for x in silences if x[1] > tt[s] - t[0] and x[0] < tt[e] - t[0]]

    if a.start is not None:
        s = int(np.searchsorted(tt, t[0] + a.start))
        if not 0 <= s < len(P) - int(20 * rate):
            raise SystemExit("--start is outside the flight or too close to its end")
        home = P[s]
        dist = np.linalg.norm(P - home, axis=1)
        after = s + int(20 * rate)                          # a loop is at least 20 s
        # The return: the first local minimum of distance back to the start, within 4 units.
        e = None
        for i in range(after, len(P) - 1):
            if dist[i] < 4.0 and dist[i] <= dist[i - 1] and dist[i] <= dist[i + 1]:
                e = i
                break
        if e is None:
            raise SystemExit("the drone never came back within 4 m of the --start point")
        bad = silent_between(s, e)
        if bad:
            raise SystemExit(f"the loop {tt[s] - t[0]:.1f}..{tt[e] - t[0]:.1f} s crosses a silence: {bad}")
        print(f"loop from {tt[s] - t[0]:.1f} s to {tt[e] - t[0]:.1f} s ({tt[e] - tt[s]:.2f} s), "
              f"closes within {dist[e]:.2f} m")
        write(a, P, tt, s, e, rate, label="loop")
        return

    # Lap boundaries: close passes of the start gate after the race began.
    i0 = int(np.searchsorted(tt, first_gate))
    gate = P[i0]
    d = np.linalg.norm(P - gate, axis=1)
    near = d < a.gate_radius
    passes = [i0]
    i = i0 + int(5 * rate)                   # a lap is longer than five seconds
    while i < len(tt):
        if near[i]:
            j = i
            while j < len(tt) and near[j]:
                j += 1
            passes.append(i + int(np.argmin(d[i:j])))
            i = j + int(5 * rate)
        else:
            i += 1
    laps = [(passes[n], passes[n + 1]) for n in range(len(passes) - 1)]
    for n, (s, e) in enumerate(laps, 1):
        print(f"lap {n}: {tt[e] - tt[s]:6.2f} s")
    if not laps:
        raise SystemExit("no complete lap found")
    if a.all:
        s, e = laps[0][0], laps[-1][1]
    else:
        if not 1 <= a.lap <= len(laps):
            raise SystemExit(f"--lap {a.lap}: there are {len(laps)} lap(s)")
        s, e = laps[a.lap - 1]
    bad = silent_between(s, e)
    if bad:
        raise SystemExit(f"that stretch crosses a silence {bad}: pick another lap or use --start")
    write(a, P, tt, s, e, rate, label="race" if a.all else f"lap {a.lap}")


def write(a, P, tt, s, e, rate, label):
    look = int(round(a.lookahead * rate))
    step = max(1, int(round(rate / a.keys_per_second)))
    poses = []
    idx = list(range(s, e + 1, step))
    if idx[-1] != e:
        idx.append(e)                    # end on the window's last sample, so a loop closes
    for i in idx:
        tgt = P[min(i + look, len(P) - 1)]
        if np.linalg.norm(tgt - P[i]) < 0.5:          # hovering: keep looking the same way
            tgt = poses[-1]["target"] if poses else (P[i] + [0, 0, 1]).tolist()
            tgt = np.array(tgt)
        frame = int(round((tt[i] - tt[s]) * a.frame_rate))
        poses.append({"frame": frame, "position": P[i].round(4).tolist(),
                      "target": np.asarray(tgt).round(4).tolist(), "fov": a.fov})
    frames = poses[-1]["frame"] + 1
    json.dump({"frameRate": a.frame_rate, "frames": frames, "poses": poses}, open(a.out, "w"))
    lo, hi = P[s:e + 1].min(0), P[s:e + 1].max(0)
    print(f"{label}: {tt[e] - tt[s]:.2f} s -> {len(poses)} keys over "
          f"{frames} frames at {a.frame_rate} fps; path spans x {lo[0]:.1f}..{hi[0]:.1f} "
          f"y {lo[1]:.1f}..{hi[1]:.1f} z {lo[2]:.1f}..{hi[2]:.1f}")
    print(f"wrote {a.out}")


if __name__ == "__main__":
    main()
