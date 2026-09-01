# Shared remote-box settings for the Windows SSH helpers.
# Source from tools/*.sh after ROOT is set.
#
#   . "$ROOT/tools/_remote.sh"
#
# Put machine-specific values in tools/local.env (gitignored). Copy
# tools/local.env.example to start. Nothing in this repo names a host or a
# Windows user; those live only in that file and in $USERPROFILE at runtime.
if [ -f "$ROOT/tools/local.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "$ROOT/tools/local.env"
  set +a
fi
: "${VDGS_HOST:?set VDGS_HOST to user@host of the Windows box, or copy tools/local.env.example to tools/local.env}"
HOST="$VDGS_HOST"

# Everything this repo puts on the Windows box lives under one directory in the remote
# %USERPROFILE%. It used to scatter ~30 loose vdgs-* entries across the home folder,
# where a staging directory, a 3 GB backup and a one-off log looked alike.
#
# scp paths are home-relative - OpenSSH on Windows resolves them under %USERPROFILE% -
# so they read "$HOST:$REMOTE_ROOT/...". The .ps1 helpers compute the same directory
# from $env:USERPROFILE, and the inline PowerShell these scripts ssh over uses
# $REMOTE_ROOT_PS, which expands to that same Join-Path expression.
REMOTE_ROOT="VDGS"
REMOTE_ROOT_PS="(Join-Path \$env:USERPROFILE '$REMOTE_ROOT')"

# scp will not create the directory it writes into, so anything that scp's straight to
# $REMOTE_ROOT has to call this first.
remote_root_mkdir() {
  ssh -o BatchMode=yes "$HOST" \
    "New-Item -ItemType Directory -Force -Path $REMOTE_ROOT_PS | Out-Null" >/dev/null 2>&1
}
