#!/usr/bin/env bash
# Collects everything needed to tell apart the ways "the mod is installed but nothing
# appears" can happen on macOS, and prints it as one block to paste back.
#
#   curl -sSL https://raw.githubusercontent.com/Saqoosha/VDGS/master/tools/collect-mac-diagnostics.sh | bash
#
# Piped rather than `bash <(...)`: process substitution is a bashism, and fish - which is
# what a terminal here is as likely to be running as not - rejects it outright.
#
# Reads only. Writes nothing, changes nothing, and prints no path outside the game folder
# except the two version strings at the top.
#
# What the answers mean, in the order the checks run - the first "no" is the diagnosis:
#
#   no game found          the companion never installed anything, or installed elsewhere
#   no vdgs-probe.log      the plugin never ran: BepInEx did not inject. Either the game
#                          was started outside the companion (its own launcher does not
#                          inject) or the loader failed - the preloader log says which
#   shaders NOT READY      the plugin ran but cannot draw. On Metal this should not happen
#   no binding for '<x>'   the plugin ran, the shaders are fine, and the track it read is
#                          not the one the capture is bound to. Compare the two spellings
#   nothing at all in the  the plugin never saw a track change: the name could not be read
#   track log              from this build of the game
set -uo pipefail

say() { printf '\n===== %s =====\n' "$1"; }

say "machine"
sw_vers | sed 's/^/  /'
uname -m | sed 's/^/  arch: /'

say "game"
GAME=""
for d in "$HOME/Library/Application Support/PatchKit/Apps"/*/Data; do
  [ -x "$d/velocidrone.app/Contents/MacOS/velocidrone" ] || continue
  GAME="$d"
done
[ -n "$GAME" ] || { echo "  no VelociDrone found under PatchKit"; exit 0; }
echo "  found"
defaults read "$GAME/velocidrone.app/Contents/Info" CFBundleShortVersionString 2>/dev/null | sed 's/^/  game version: /'
codesign -dv "$GAME/velocidrone.app" 2>&1 | grep -E "^(Identifier|CodeDirectory)" | sed 's/^/  /'

say "what is installed"
for f in libdoorstop.dylib BepInEx/core/BepInEx.Preloader.dll BepInEx/plugins/VDGS.dll vdgs/vdgs-shaders; do
  if [ -e "$GAME/$f" ]; then printf '  %-38s %s bytes\n' "$f" "$(wc -c < "$GAME/$f" | tr -d ' ')"
  else printf '  %-38s MISSING\n' "$f"; fi
done
echo "  loader stamp: $(cat "$GAME/BepInEx/vdgs-bepinex-version.txt" 2>/dev/null || echo 'none')"
# Gatekeeper refuses to insert a quarantined dylib, and refuses silently.
echo "  quarantine on libdoorstop.dylib: $(xattr "$GAME/libdoorstop.dylib" 2>/dev/null | grep -c quarantine || true)"

say "captures and bindings"
ls -1 "$GAME/vdgs" 2>/dev/null | sed 's/^/  /' || echo "  no vdgs/ folder"
echo "  --- bindings.json"
cat "$GAME/vdgs/bindings.json" 2>/dev/null | sed 's/^/  /' || echo "  none"

say "did the plugin run at all"
if [ -f "$GAME/vdgs-probe.log" ]; then
  echo "  yes - vdgs-probe.log exists"
  head -1 "$GAME/vdgs-probe.log" | sed 's/^/  /'
  grep -E "^graphicsDeviceType|^shaderLevel" "$GAME/vdgs-probe.log" | tail -2 | sed 's/^/  /'
  # The shader verdict decides whether anything can be drawn at all, so only the last
  # bundle report is kept - the file carries one per scene load and they are identical.
  awk '/======== shader bundle/{buf=""} {buf=buf"\n"$0}
       END{print buf}' "$GAME/vdgs-probe.log" \
    | grep -E "bundle size|MISSING|shader '|compute '|=> shaders" | sed 's/^/  /'
  # Only the web UI's Show writes a "show" block; a capture that appeared because its
  # track was selected is recorded in vdgs-track.log instead, so this counts one route,
  # not all of them.
  grep -c "======== show " "$GAME/vdgs-probe.log" | sed 's/^/  shown by hand from the web UI: /'
  # Which camera the splat pass can attach to. A capture that spawns, costs frame time and
  # still shows nothing has been rasterised somewhere the screen is not.
  say "cameras the last scene offered"
  awk '/^-- cameras/{f=1} f&&!/^-- cameras/{if(/^--/)f=0; else print}' "$GAME/vdgs-probe.log" \
    | tail -8 | sed 's/^/  /'
else
  echo "  NO - the plugin never ran. BepInEx did not inject."
  echo "  --- preloader errors, if any"
  cat "$GAME"/preloader_*.log "$GAME/velocidrone.app/Contents/MacOS"/preloader_*.log 2>/dev/null \
    | head -25 | sed 's/^/  /' || echo "  none"
fi

say "what the mod saw of the track"
tail -40 "$GAME/vdgs-track.log" 2>/dev/null | sed 's/^/  /' || echo "  no vdgs-track.log"

say "loader log"
grep -vE "KEyeHistogramClear|PostProcessing|^\s*at " "$GAME/BepInEx/LogOutput.log" 2>/dev/null \
  | tail -25 | sed 's/^/  /' || echo "  no BepInEx/LogOutput.log"

say "frame times, if it ever drew"
tail -6 "$GAME/vdgs-perf.log" 2>/dev/null | sed 's/^/  /' || echo "  no vdgs-perf.log"
