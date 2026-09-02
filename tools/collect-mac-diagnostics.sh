#!/usr/bin/env bash
# Packs every log the mod and the game wrote into one zip on the Desktop, and prints a
# short summary so the obvious cases are answered without opening it.
#
#   curl -sSL https://raw.githubusercontent.com/Saqoosha/VDGS/master/tools/collect-mac-diagnostics.sh | bash
#
# Piped rather than `bash <(...)`: process substitution is a bashism, and fish - which a
# terminal here is as likely to be running as not - rejects it outright.
#
# Reads only. Nothing is changed, nothing is uploaded; the zip is left for you to send.
# It is small - about 15 KB - because these are text logs.
#
# The summary is a shortcut, not the evidence. Whole files go in the zip because a grep
# written in advance only finds the failures somebody already thought of, and the ones
# that cost days here have all been the other kind.
set -uo pipefail

say() { printf '\n===== %s =====\n' "$1"; }

OUT="$HOME/Desktop/vdgs-logs-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"

say "machine"
sw_vers | sed 's/^/  /' | tee "$OUT/machine.txt"
uname -m | sed 's/^/  arch: /' | tee -a "$OUT/machine.txt"
system_profiler SPDisplaysDataType 2>/dev/null \
  | grep -E "Chipset Model|Total Number of Cores|Resolution" | sed 's/^ */  /' \
  | tee -a "$OUT/machine.txt"

say "game"
GAME=""
for d in "$HOME/Library/Application Support/PatchKit/Apps"/*/Data; do
  [ -x "$d/velocidrone.app/Contents/MacOS/velocidrone" ] || continue
  GAME="$d"
done
[ -n "$GAME" ] || {
  echo "  no VelociDrone found under PatchKit - nothing to collect"
  rmdir "$OUT" 2>/dev/null
  exit 0
}
echo "  found"
defaults read "$GAME/velocidrone.app/Contents/Info" CFBundleShortVersionString 2>/dev/null \
  | sed 's/^/  version: /'

say "what is installed"
for f in libdoorstop.dylib BepInEx/core/BepInEx.Preloader.dll BepInEx/plugins/VDGS.dll vdgs/vdgs-shaders; do
  if [ -e "$GAME/$f" ]; then printf '  %-38s %s bytes\n' "$f" "$(wc -c < "$GAME/$f" | tr -d ' ')"
  else printf '  %-38s MISSING\n' "$f"; fi
done
echo "  loader stamp: $(cat "$GAME/BepInEx/vdgs-bepinex-version.txt" 2>/dev/null || echo none)"
# Gatekeeper refuses to insert a quarantined dylib, and refuses silently.
echo "  quarantine on libdoorstop.dylib: $(xattr "$GAME/libdoorstop.dylib" 2>/dev/null | grep -c quarantine || true)"

say "captures and bindings"
ls -1 "$GAME/vdgs" 2>/dev/null | sed 's/^/  /'
cat "$GAME/vdgs/bindings.json" 2>/dev/null | sed 's/^/  /' || echo "  no bindings.json"

# ------------------------------------------------------------------ the zip
# Everything, whole. placement.json and meta.json go in as well: where a capture sits and
# how it was packed decide what a camera sees, and both are a few hundred bytes.
for f in vdgs-probe.log vdgs-track.log vdgs-perf.log BepInEx/LogOutput.log; do
  [ -f "$GAME/$f" ] && cp "$GAME/$f" "$OUT/$(basename "$f")"
done
cp "$GAME"/preloader_*.log "$OUT/" 2>/dev/null
cp "$GAME/velocidrone.app/Contents/MacOS"/preloader_*.log "$OUT/" 2>/dev/null
cp "$GAME/vdgs/bindings.json" "$OUT/" 2>/dev/null
for d in "$GAME"/vdgs/*/; do
  name=$(basename "$d")
  [ "$name" = "ui" ] && continue
  [ -f "$d/meta.json" ] && cp "$d/meta.json" "$OUT/$name.meta.json"
  [ -f "$d/placement.json" ] && cp "$d/placement.json" "$OUT/$name.placement.json"
done
# The game's own log. Under -force-d3d12 on Windows this reaches tens of megabytes; on
# Metal it stays small, but trim anyway so a long session cannot make the zip unsendable.
PL="$HOME/Library/Logs/velocidrone/velocidrone/Player.log"
[ -f "$PL" ] && tail -c 400000 "$PL" > "$OUT/Player.log"

say "what the mod saw of the track"
tail -25 "$GAME/vdgs-track.log" 2>/dev/null | sed 's/^/  /' || echo "  no vdgs-track.log"

say "did it ever draw"
tail -4 "$GAME/vdgs-perf.log" 2>/dev/null | sed 's/^/  /' || echo "  no vdgs-perf.log"
echo "  (columns: time / fps / avg ms / worst ms / splats / scenes shown)"

ZIP="$OUT.zip"
( cd "$(dirname "$OUT")" && zip -q -r "$ZIP" "$(basename "$OUT")" ) && rm -rf "$OUT"
say "send this file"
echo "  $ZIP  ($(wc -c < "$ZIP" | tr -d ' ') bytes)"
echo "  Also useful: a screenshot taken while flying the track."
