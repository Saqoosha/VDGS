#!/usr/bin/env python3
"""Build the VelociDrone course for JDL 2026 R7 from the scan and the flown course line.

The gates are invisible prefabs, placed on the arches and flags that stand in the scan.
Everything here is measured from the race-time prefabs in the game's `invisible` bundle:

  DefaultSquare (88)             pivot at the bottom centre. Trigger plate is 1.93 m wide on local Y and runs 0.03-1.95 m up
                                 local Z. Direction is checked: the drone must fly along local -X.
  DefaultInvisibleFlagPole (2231) at scale 100 (10000 in the file) the trigger is a plate 25 m long on local -X from the pole,
                                 about 16 m tall. flagGate = 1, so either direction counts.

So a flag's plate points away from the pole, across the line the drone flies. `rot` is (w, x, y, z) x 1000 and `pos` is
centimetres. Both are in Unity's frame; the scan is x east, y up, z south, which a Z mirror takes to Unity.

    python3 tools/make_r7_track.py <course.json> <fit.json> <out.track.json> [--check]

course.json is ghostline's rt/tracks/jdl-2026-r7-v1.json, for the line and the launch pad. fit.json is what was measured from
the scan's splats: each flag's base, each arch's legs (centre, width direction, inner width) and banner underside, and the
ground under each.
--check flies the course line through the finished triggers and fails unless every gate is hit in order.
"""
import json
import math
import sys

import numpy as np

SQUARE, POLE, GRID = 88, 2231, 422
OFFSET = np.array([-70.0, 0.0, 90.0])   # placement of the scan in the game; y is filled in from the ground
NAME = "VDGS+JDL+2026+R7"
SCENE_ID = 16                           # BlankCanvas, as every other VDGS track
SQUARE_W, SQUARE_TOP, SQUARE_BOTTOM = 1.928, 1.954, 0.026
PLATE_LEN, PLATE_LOW, PLATE_HIGH = 24.9, -1.5, 14.3

# The flags the course turns around, in flying order, with the index on the course line where the drone passes each one.
# 64 is looped, so it gets a plate at each of three points around it: passing only one would let a pilot skip the loop.
FLAGS = {
    "f80": [206], "f64": [298, 322, 352], "f43": [456], "f29": [550], "f15": [598], "f11": [703],
    "f37": [1000], "f20": [1075], "f23": [1208], "f46": [1353], "f86": [1524],
}
# B is an over-and-under: the line goes north OVER
# the arch at 4.9 m, turns behind it, and comes back south through it. The over pass is a square standing on top of the arch.
OVER_SIZE = 6.0
FIRST_FLAG = "p68"


def to_unity(x, y, z):
    return np.array([x, y, -z]) + OFFSET


def quat_from_matrix(m):
    t = np.trace(m)
    if t > 0:
        s = math.sqrt(t + 1) * 2
        return np.array([s / 4, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s])
    i = int(np.argmax(np.diag(m)))
    j, k = (i + 1) % 3, (i + 2) % 3
    s = math.sqrt(1 + m[i, i] - m[j, j] - m[k, k]) * 2
    q = np.zeros(4)
    q[0] = (m[k, j] - m[j, k]) / s
    q[1 + i] = s / 4
    q[1 + j] = (m[j, i] + m[i, j]) / s
    q[1 + k] = (m[k, i] + m[i, k]) / s
    return q


def matrix_from_quat(q):
    w, x, y, z = np.array(q, float) / np.linalg.norm(q)
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def rot_for(minus_x):
    """The rotation whose local -X is this horizontal direction, with local Z up. Columns are the local axes in the world.

    Unity is left-handed, and a rotation matrix keeps its determinant either way, so Y = Z x X holds in the numbers."""
    ax = -np.array(minus_x, float)
    ax[1] = 0
    ax /= np.linalg.norm(ax)
    az = np.array([0.0, 1.0, 0.0])
    ay = np.cross(az, ax)
    m = np.c_[ax, ay, az]
    return [int(round(v * 1000)) for v in quat_from_matrix(m)]


def trans(pos, rot, scale):
    return {"pos": [int(round(v * 100)) for v in pos], "rot": rot, "scale": scale}


def gate(prefab, tr, n, start=False, finish=False, lap1only=False):
    return {"prefab": prefab, "trans": tr, "gate": n, "start": start, "finish": finish, "lap1only": lap1only}


def tangent(line, i):
    a, b = line[max(i - 3, 0)], line[min(i + 3, len(line) - 1)]
    return to_unity(*b) - to_unity(*a)


def arch(line, fit, at, n, over=False, **flags):
    (cx, cz), (wx, wz) = fit["centre"], fit["width_dir"]
    fwd = np.array([wz, 0.0, wx])              # across the arch, in Unity's frame (z is mirrored)
    if fwd @ tangent(line, at) < 0:
        fwd = -fwd
    width, height, base = fit["inner"], fit["under"], fit["ground"]
    if over:
        base, width, height = fit["ground"] + fit["top"], OVER_SIZE, OVER_SIZE
    sy, sz = width / SQUARE_W, height / (SQUARE_TOP - SQUARE_BOTTOM)
    pos = to_unity(cx, base, cz)
    return gate(SQUARE, trans(pos, rot_for(fwd), [100, int(round(sy * 100)), int(round(sz * 100))]), n, **flags)


def flag(line, fit, at, n):
    pole = to_unity(fit["base"][0], fit["ground"], fit["base"][1])
    return gate(POLE, trans(pole, rot_for(to_unity(*line[at]) - pole), [10000, 10000, 10000]), n)


def build(course, fit):
    line = np.array(course["track"])
    OFFSET[1] = -fit["_course"]
    seq = [("start", 0, dict(start=True, finish=True))]
    seq += [(name, a, {}) for name, ats in FLAGS.items() for a in ats]
    seq += [("A", 654, {}), ("B", 868, dict(over=True)), ("B", 912, {}), ("C", 1457, {})]
    seq.sort(key=lambda s: s[1])
    lc = course["launch"]["centre"]
    pad = to_unity(lc[0], fit["launch"], lc[2])
    # The first flag is rounded once, between the pads and the first start. No course line covers that leg, so its plate points
    # away from both: going round the pole either way crosses it.
    first = fit[FIRST_FLAG]
    pole = to_unity(first["base"][0], first["ground"], first["base"][1])
    start = to_unity(fit["start"]["centre"][0], fit["start"]["ground"], fit["start"]["centre"][1])
    gates = [gate(POLE, trans(pole, rot_for(pole - (pad + start) / 2), [10000, 10000, 10000]), 0, lap1only=True)]
    for n, (name, at, kw) in enumerate(seq, 1):
        gates.append(flag(line, fit[name], at, n) if name in FLAGS else arch(line, fit[name], at, n, **kw))
    aim = to_unity(*line[-60]) - pad
    aim[1] = 0
    aim /= np.linalg.norm(aim)
    ang = math.atan2(aim[0], aim[2]) / 2
    grid = {"prefab": GRID, "trans": trans(pad, [int(round(math.cos(ang) * 1000)), 0, int(round(math.sin(ang) * 1000)), 0], [100, 100, 100])}
    return gates, grid, line, seq


def crossings(a, b, g):
    """Where the segment a-b goes through this gate's trigger, as a fraction along it, or None."""
    t = g["trans"]
    pos = np.array(t["pos"], float) / 100
    m = matrix_from_quat(t["rot"])
    s = np.array(t["scale"], float) / 100
    la, lb = m.T @ (a - pos), m.T @ (b - pos)
    if g["prefab"] == SQUARE:
        axis, u, v = 0, (1, -SQUARE_W / 2 * s[1], SQUARE_W / 2 * s[1]), (2, SQUARE_BOTTOM * s[2], SQUARE_TOP * s[2])
    else:
        axis, u, v = 1, (0, -PLATE_LEN, 0.0), (2, PLATE_LOW, PLATE_HIGH)
    if la[axis] * lb[axis] > 0 or la[axis] == lb[axis]:
        return None
    f = la[axis] / (la[axis] - lb[axis])
    p = la + (lb - la) * f
    if not (u[1] <= p[u[0]] <= u[2] and v[1] <= p[v[0]] <= v[2]):
        return None
    if g["prefab"] == SQUARE and (lb[0] - la[0]) > 0:   # squares only count along local -X
        return None
    return f


def check(gates, line):
    """Fly two laps of the course line; each gate must fire in turn. Prints where, returns the number missed.

    The line starts just past the start gate, so two laps fire it at the end of each: one full lap plus that first gate."""
    pts = np.array([to_unity(*p) for p in line])
    pts = np.r_[pts, pts[:1]]
    order = sorted((g for g in gates if not g["lap1only"]), key=lambda g: g["gate"])
    nxt, hits = 0, []
    for lap in range(2):
        for i in range(len(pts) - 1):
            while True:
                f = crossings(pts[i], pts[i + 1], order[nxt % len(order)])
                if f is None:
                    break
                hits.append((lap, i, order[nxt % len(order)]["gate"]))
                nxt += 1
    print(f"{nxt} gates fired in 2 laps, {len(order)} per lap")
    for h in hits[:len(order) + 1]:
        print(f"  lap {h[0]} line #{h[1]:4d} gate {h[2]}")
    return len(order) + 1 - nxt


def main():
    args = [a for a in sys.argv[1:] if a != "--check"]
    course, fit = json.load(open(args[0])), json.load(open(args[1]))
    gates, grid, line, seq = build(course, fit)
    value = json.dumps({"gates": gates, "barriers": [grid]}, separators=(",", ":"))
    json.dump({"name": NAME, "scene_id": SCENE_ID, "type": 0, "value": value}, open(args[2], "w"), indent=2)
    print(f"{len(gates)} gates, placement position {OFFSET.round(3).tolist()}")
    if "--check" in sys.argv:
        missed = check(gates, line)
        if missed:
            sys.exit(f"{missed} gates missed")


if __name__ == "__main__":
    main()
