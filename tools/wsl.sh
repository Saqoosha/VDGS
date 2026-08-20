#!/usr/bin/env bash
# Run a local script inside WSL on `w`, where vdb_tool and the decimator live.
#
#   bash tools/wsl.sh script.sh [root]
#
# Mesh generation cannot run on the Mac: Homebrew's openvdb formula does not pass
# -DOPENVDB_BUILD_VDB_TOOL=ON and the CMake default is OFF, so the bottle ships
# vdb_print but not vdb_tool. WSL has it from apt (libopenvdb-tools, 10.6.1).
#
# Quoting does not survive Mac bash -> ssh -> PowerShell -> wsl.exe -> bash, and the
# failures are silent syntax errors rather than anything that names the real problem.
# Base64 sidesteps every layer: one opaque token goes across, and bash decodes it.
#
# The staging path is per-invocation. A fixed /tmp/_run.sh gets written as root on a root
# run and is then unwritable for the next user run, which fails as "Permission denied"
# on a line the script does not contain.
#
#   bash wslrun.sh script.sh [root]
set -euo pipefail
SCRIPT="${1:?usage: wslrun.sh <script> [root]}"
USER_ARG=""
[ "${2:-}" = "root" ] && USER_ARG="-u root"
B64="$(base64 < "$SCRIPT" | tr -d '\n')"
TAG="$(basename "$SCRIPT" .sh)-$$"
ssh -o ConnectTimeout=15 user@windows-box \
  "wsl -d Ubuntu-24.04 $USER_ARG -e bash -c 'f=\$(mktemp /tmp/wslrun-$TAG-XXXX.sh); echo $B64 | base64 -d > \$f; bash \$f; rc=\$?; rm -f \$f; exit \$rc'" \
  2>&1 | grep -v "WARNING: connection is not using\|store now, decrypt later\|openssh.com/pq"
