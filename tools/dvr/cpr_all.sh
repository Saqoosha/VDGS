#!/bin/bash
# CPR on every frame (win4090 WSL, run as a scheduled task: ~2 h for 5,564 frames): round 1 (3 yaw candidates from run 10) -> fuse -> round 2 (1 view from the fused track) -> fuse.
export CUDA_HOME=/usr/local/cuda-12.9 PATH=/usr/local/cuda-12.9/bin:$PATH
cd /mnt/c/Users/saqoosha/JDL-2026-R6/dvr
PLY=/mnt/c/Users/saqoosha/JDL-2026-R6-fix/out/JDL-2026-R6-fix-web.ply
round() {   # $1 name, $2 start poses, $3 yaws
  YAWS=$3 ~/gsenv/bin/python cpr_render.py $PLY dvr_pinhole.mp4.json $2 frames_all.txt ~/$1 > $1_render.log 2>&1 &
  ~/mastenv/bin/python cpr_match.py dvr_pinhole.mp4 ~/$1 frames_all.txt $1.jsonl > $1_match.log 2>&1
  wait
  ~/mastenv/bin/python cpr_fuse.py poses60_refined10.json $1.jsonl poses_$1.json > $1_fuse.log 2>&1
}
round cprall1 poses60_refined10.json 0,-25,25
round cprall2 poses_cprall1.json 0
echo ALL-DONE > cprall.done
