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
