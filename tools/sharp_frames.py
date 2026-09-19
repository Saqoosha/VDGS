#!/usr/bin/env python3
"""Pick the sharpest frame in every window of a video and write them as JPEGs.

Replicates what AirVis Studio does for video input (`sharpnessCandidateCount`) so
that an image folder can be handed over instead of the whole MP4. Sharpness is
the variance of the Laplacian on a downscaled grey frame; one frame per
`--fps` window (1/fps seconds) wins.

    python3 tools/sharp_frames.py in.mp4 out_dir --fps 2 --prefix a --quality 3

Two passes over the video: the first scores every frame at 1/4 size, the second
re-decodes and writes only the winners at full resolution. Both use ffmpeg.
"""
import argparse
import json
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

import cv2
import numpy as np


def probe(path):
    out = subprocess.check_output([
        "ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,r_frame_rate,nb_frames",
        "-of", "json", path,
    ])
    s = json.loads(out)["streams"][0]
    num, den = s["r_frame_rate"].split("/")
    return int(s["width"]), int(s["height"]), float(num) / float(den), int(s["nb_frames"])


def score_frames(path, w, h, scale):
    sw, sh = w // scale, h // scale
    cmd = [
        "ffmpeg", "-v", "error", "-hwaccel", "videotoolbox", "-i", path,
        "-vf", f"scale={sw}:{sh}", "-pix_fmt", "gray", "-f", "rawvideo", "-",
    ]
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, bufsize=sw * sh * 64)
    scores = []
    n = sw * sh
    while True:
        buf = p.stdout.read(n)
        if len(buf) < n:
            break
        g = np.frombuffer(buf, np.uint8).reshape(sh, sw)
        scores.append(float(cv2.Laplacian(g, cv2.CV_64F).var()))
        if len(scores) % 1000 == 0:
            print(f"  scored {len(scores)}", file=sys.stderr)
    p.wait()
    return np.array(scores)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("video")
    ap.add_argument("out_dir")
    ap.add_argument("--fps", type=float, default=2.0, help="winners per second")
    ap.add_argument("--scale", type=int, default=4, help="downscale for scoring")
    ap.add_argument("--prefix", default="f")
    ap.add_argument("--quality", type=int, default=3, help="ffmpeg -q:v (2 best)")
    ap.add_argument("--scores", help="per-frame scores cache (json); reused if it exists")
    ap.add_argument("--jobs", type=int, default=6, help="parallel ffmpeg writers")
    a = ap.parse_args()

    w, h, fps, nb = probe(a.video)
    print(f"{a.video}: {w}x{h} {fps:.3f}fps {nb} frames", file=sys.stderr)
    if a.scores and os.path.exists(a.scores):
        scores = np.array(json.load(open(a.scores))["scores"])
        print(f"reusing {len(scores)} scores from {a.scores}", file=sys.stderr)
    else:
        scores = score_frames(a.video, w, h, a.scale)
    win = fps / a.fps
    picks = []
    start = 0.0
    while start < len(scores):
        lo, hi = int(round(start)), min(int(round(start + win)), len(scores))
        if hi > lo:
            picks.append(lo + int(np.argmax(scores[lo:hi])))
        start += win
    if a.scores:
        with open(a.scores, "w") as f:
            json.dump({"scores": scores.tolist(), "picks": picks}, f)
    med = np.median(scores)
    print(f"picked {len(picks)} of {len(scores)}; median score all {med:.1f}, "
          f"picked {np.median(scores[picks]):.1f}", file=sys.stderr)

    os.makedirs(a.out_dir, exist_ok=True)
    # One ffmpeg per winner, seeking by timestamp. A single `select` expression
    # with hundreds of eq() terms overflows ffmpeg's expression parser
    # ("Cannot allocate memory" at ~600 terms), so this is not an optimisation
    # target: it is the way that works.
    def grab(i):
        dst = os.path.join(a.out_dir, f"{a.prefix}_{i:06d}.jpg")
        subprocess.check_call([
            "ffmpeg", "-v", "error", "-hwaccel", "videotoolbox",
            "-ss", f"{i / fps:.6f}", "-i", a.video, "-frames:v", "1",
            "-q:v", str(a.quality), "-y", dst,
        ])
        return dst
    with ThreadPoolExecutor(max_workers=a.jobs) as ex:
        for k, _ in enumerate(ex.map(grab, picks), 1):
            if k % 100 == 0:
                print(f"  wrote {k}/{len(picks)}", file=sys.stderr)
    print(f"wrote {len(picks)} frames to {a.out_dir}", file=sys.stderr)


if __name__ == "__main__":
    main()
