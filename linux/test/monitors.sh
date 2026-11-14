#!/usr/bin/env bash
# Monitor and scale changes on GNOME Wayland + XWayland (the x11 pin backend), in a throwaway headless GNOME Shell with
# two virtual monitors and Mutter's xwayland-native-scaling: a laptop panel at scale 2 and an external monitor at scale 1.
# A tab group of two widgets (scale 0.81) sits on the scale-2 panel. Then the panel is switched off (what undocking or a
# resume with the lid closed does): XWayland's scale drops from 2 to 1, and every widget window and its tab strip must be
# re-placed on the remaining monitor at the logical size x widget scale, with the saved monitor kept in settings.json.
# Switching the panel back on must put the widgets back on it. Before the fix Mutter kept each window at its old device
# size: the X11 geometry stayed 2x and the screenshot showed a large stale copy behind the right-sized one.
# Everything (HOME, XDG_*, D-Bus, dconf, gsd-xsettings for GTK's X11 scale) is throwaway; the monitor layout is changed
# through that shell's DisplayConfig (monitor-config.py). Screenshots are taken only inside this headless shell.
#
#   dotnet build Umbra.Linux.slnf && linux/test/monitors.sh
#
# UMBRA_KEEP=1 keeps the temp folder (screen-*.png, umbra.log); UMBRA_X11_WINDOW_TYPE=utility tests the utility window type.
# Needs gnome-shell 46+ (Mutter 47+ for native XWayland scaling), gsd-xsettings, python3-gi, xwininfo, xdotool.
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
config=${UMBRA_CONFIGURATION:-Debug}
bin="$repo/src/Umbra.Linux/bin/$config/net10.0/umbra"
ctl="$repo/src/Umbra.Cli/bin/$config/net10.0/umbractl.dll"

if [ "${UMBRA_MON_INSIDE:-}" = "" ]; then
  for tool in gnome-shell dbus-run-session gdbus gsettings xwininfo xdotool python3; do command -v $tool >/dev/null || { echo "monitors.sh: $tool is not installed" >&2; exit 2; }; done
  [ -x "$bin" ] || { echo "monitors.sh: $bin not found; run: dotnet build Umbra.Linux.slnf" >&2; exit 2; }
  tmp=$(mktemp -d "${TMPDIR:-/tmp}/umbra-monitors-XXXXXX")
  chmod 700 "$tmp"
  # The session's document portal and gvfs mount FUSE folders under XDG_RUNTIME_DIR: unmount them before removing it.
  cleanup() {
    if [ "${UMBRA_KEEP:-}" = 1 ]; then echo "kept: $tmp" >&2; return; fi
    for m in "$tmp/run/doc" "$tmp/run/gvfs"; do fusermount -uz "$m" 2>/dev/null || fusermount3 -uz "$m" 2>/dev/null; done
    rm -rf "$tmp" 2>/dev/null || { sleep 1; rm -rf "$tmp"; }
  }
  trap cleanup EXIT
  mkdir -p "$tmp/home" "$tmp/config" "$tmp/data/gnome-shell/extensions" "$tmp/cache" "$tmp/state" "$tmp/run" "$tmp/profile/widgets" "$tmp/bin"
  chmod 700 "$tmp/run"
  cp -r "$here/gnome-eval-extension/umbra-test-eval@umbra.test" "$tmp/data/gnome-shell/extensions/"
  cp -r "$repo/widgets/clock" "$repo/widgets/pomodoro" "$tmp/profile/widgets/"
  printf '#!/bin/sh\nprintf "%%s\\n" "$1" >> "%s"\n' "$tmp/opened.txt" > "$tmp/bin/xdg-open"; chmod +x "$tmp/bin/xdg-open"
  # The update check reads a local old-release feed instead of api.github.com (UMBRA_RELEASE_URL, Debug builds).
  printf '{ "tag_name": "v0.0.1", "draft": false, "prerelease": false, "body": "", "assets": [] }\n' > "$tmp/no-update.json"
  env -i PATH="$tmp/bin:$PATH" HOME="$tmp/home" XDG_CONFIG_HOME="$tmp/config" XDG_DATA_HOME="$tmp/data" XDG_CACHE_HOME="$tmp/cache" \
    XDG_STATE_HOME="$tmp/state" XDG_RUNTIME_DIR="$tmp/run" XDG_CURRENT_DESKTOP=GNOME XDG_SESSION_TYPE=wayland LANG=C.UTF-8 \
    ${DOTNET_ROOT:+DOTNET_ROOT="$DOTNET_ROOT"} DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    UMBRA_RELEASE_URL="$tmp/no-update.json" UMBRA_X11_WINDOW_TYPE="${UMBRA_X11_WINDOW_TYPE:-}" UMBRA_MON_INSIDE=1 UMBRA_TMP="$tmp" TMPDIR="$tmp" UMBRA_DATA_DIR="$tmp/profile" UMBRA_BIN="$bin" UMBRACTL="dotnet $ctl" \
    dbus-run-session -- "$0"
  code=$?
  if [ $code -ne 0 ]; then
    echo "--- umbra.log (last 30 lines)" >&2; tail -n 30 "$tmp/profile/umbra.log" >&2 2>/dev/null
    echo "--- gnome-shell (last 20 lines)" >&2; tail -n 20 "$tmp/shell.log" >&2 2>/dev/null
  fi
  exit $code
fi

# ----- inside the throwaway D-Bus session -----
tmp=$UMBRA_TMP
failed=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failed=1; fi; }
ev() { gdbus call --session --dest org.gnome.Shell --object-path /org/gnome/Shell --method org.gnome.Shell.Eval "$1" 2>/dev/null; }
js() { ev "$1" | sed -n "s/^(true, '\(.*\)')$/\1/p" | tr -d '\\' | sed 's/^"\(.*\)"$/\1/'; }
mon() { python3 "$here/monitor-config.py" "$@"; }
shot() { js 'const S = imports.gi.Shell; new S.Screenshot().screenshot(false, imports.gi.Gio.File.new_for_path("'"$tmp"'/screen-'"$1"'.png").create(0, null), () => {}); "ok"' >/dev/null; sleep 1; }
# X11 geometry (device pixels of XWayland) of a window by title: "x,y wxh".
geo() { local w; w=$(xdotool search --name "$1" 2>/dev/null | head -1); [ -n "$w" ] && xwininfo -id "$w" | awk '/Absolute upper-left X/ {x=$4} /Absolute upper-left Y/ {y=$4} /Width:/ {w=$2} /Height:/ {h=$2} END {print x","y" "w"x"h}'; }
# Device geometry divided by the XWayland scale: "x,y wxh" in logical pixels.
logical() { echo "$1" | awk -v k="$2" -F'[ ,x]' '{printf "%d,%d %dx%d\n", $1/k, $2/k, $3/k, $4/k}'; }
size() { echo "$1" | cut -d' ' -f2; }
ws() { python3 -c 'import json,sys; w=json.load(open(sys.argv[1]))["widgets"][sys.argv[2]]; print(w.get("monitor"), w.get("relX"), w.get("relY"))' "$UMBRA_DATA_DIR/settings.json" "$1"; }

gsettings set org.gnome.shell enabled-extensions "['umbra-test-eval@umbra.test']"
gsettings set org.gnome.shell disable-user-extensions false
gsettings set org.gnome.shell welcome-dialog-last-shown-version '9999'
# Mutter 50 always scales XWayland natively (the experimental-features of older versions are gone).
gnome-shell --headless --wayland --virtual-monitor 2880x1800 --virtual-monitor 3440x1440 --wayland-display umbra-mon-test > "$tmp/shell.log" 2>&1 &
shell=$!
for _ in $(seq 1 60); do [ "$(js '1+1')" = 2 ] && break; sleep 0.5; done
check 'the headless GNOME Shell runs with Eval' '[ "$(js "1+1")" = 2 ]'
read -r panel external < <(mon list | awk '/^Meta|^[A-Z]/ && !/^logical/ {print $1}' | tr '\n' ' ')
mon apply "$panel:2,$external:1" >/dev/null; sleep 2
mon list | sed 's/^/      /'
export WAYLAND_DISPLAY=umbra-mon-test
display=$(js 'imports.gi.GLib.getenv("DISPLAY") ?? ""'); [ -n "$display" ] && export DISPLAY=$display
xauth=$(js 'imports.gi.GLib.getenv("XAUTHORITY") ?? ""'); [ -n "$xauth" ] && export XAUTHORITY=$xauth
# The XSETTINGS daemon tells GTK on XWayland its scale (Gdk/WindowScalingFactor), as in a real GNOME session.
xsettings=/usr/libexec/gsd-xsettings
[ -x "$xsettings" ] || xsettings=/usr/lib/gnome-settings-daemon/gsd-xsettings
"$xsettings" > "$tmp/xsettings.log" 2>&1 &
xsd=$!
sleep 2

# The group sits on the panel (scale 2), 100,100 from its corner, like the report: scale 0.81, saved monitor = the panel.
cat > "$UMBRA_DATA_DIR/settings.json" <<EOF
{"general":{"logLevel":"verbose"},
 "widgets":{
  "clock":{"enabled":true,"scale":0.81,"monitor":"$panel","relX":100,"relY":100,"x":100,"y":100},
  "pomodoro":{"enabled":true,"scale":0.81,"monitor":"$panel","relX":100,"relY":100,"x":100,"y":100}},
 "groups":{"g1":{"name":"Group","members":["clock","pomodoro"],"active":"clock"}}}
EOF
"$UMBRA_BIN" --background > "$UMBRA_DATA_DIR/stdout.txt" 2>&1 &
umbra=$!
for _ in $(seq 1 40); do [ -n "$(geo 'Umbra - Clock')" ] && break; sleep 0.5; done
sleep 3
check 'umbractl talks to this throwaway Umbra' '$UMBRACTL status | grep -q "\"dataDir\": \"$UMBRA_DATA_DIR\""'
check 'Umbra uses the x11 backend (XWayland)' '$UMBRACTL status | grep -q "\"pinBackend\": \"x11"'
# X11 geometry is in XWayland's device pixels: logical x 2 on the panel, logical x 1 once only the external monitor is left.
clock1=$(geo "Umbra - Clock"); tabs1=$(geo "Umbra - widget tabs"); pomo1=$(geo "Umbra - Pomodoro")
echo "      docked (X11 scale 2):    clock $clock1, hidden tab $pomo1, tab strip $tabs1"
$UMBRACTL capture clock "$tmp/clock-1.png" >/dev/null
shot 1-docked
check "the clock is at 100,100 of the panel, logical (X11 $clock1)" '[ "$(logical "$clock1" 2 | cut -d" " -f1)" = "100,100" ]'

# Undock: only the external monitor at scale 1 is left. XWayland's scale goes from 2 to 1.
mon apply "$external:1" >/dev/null; sleep 4
mon list | sed 's/^/      /'
clock2=$(geo "Umbra - Clock"); tabs2=$(geo "Umbra - widget tabs"); pomo2=$(geo "Umbra - Pomodoro")
echo "      undocked (X11 scale 1):  clock $clock2, hidden tab $pomo2, tab strip $tabs2"
$UMBRACTL capture clock "$tmp/clock-2.png" >/dev/null
shot 2-undocked
check "the clock window is re-placed and resized for scale 1, no stale 2x frame (X11 $clock2, want $(logical "$clock1" 2))" '[ "$clock2" = "$(logical "$clock1" 2)" ]'
check "the hidden tab too (X11 $pomo2, want $(logical "$pomo1" 2))" '[ "$pomo2" = "$(logical "$pomo1" 2)" ]'
check "the tab strip too (X11 $tabs2, want $(logical "$tabs1" 2))" '[ "$tabs2" = "$(logical "$tabs1" 2)" ]'
check "the page fills the window: capture $(identify -format %wx%h "$tmp/clock-2.png" 2>/dev/null) = window $(size "$clock2")" \
  '! command -v identify >/dev/null || [ "$(identify -format %wx%h "$tmp/clock-2.png")" = "$(size "$clock2")" ]'
check "the saved monitor is still the panel ($(ws clock))" '[ "$(ws clock)" = "$panel 100 100" ]'
check 'the change is in the log' 'grep -q "Display changed" "$UMBRA_DATA_DIR/umbra.log"'

# Re-dock: the panel comes back at scale 2, the widgets go back to it.
mon apply "$panel:2,$external:1" >/dev/null; sleep 4
clock3=$(geo "Umbra - Clock"); tabs3=$(geo "Umbra - widget tabs")
echo "      re-docked (X11 scale 2): clock $clock3, tab strip $tabs3"
shot 3-redocked
check "the clock is back where it was on the panel (X11 $clock1 -> $clock3)" '[ "$clock1" = "$clock3" ]'
check "and the tab strip (X11 $tabs1 -> $tabs3)" '[ "$tabs1" = "$tabs3" ]'

$UMBRACTL quit >/dev/null 2>&1; wait $umbra 2>/dev/null
kill $xsd $shell 2>/dev/null; wait $shell 2>/dev/null
[ $failed = 0 ] && echo "MONITORS TEST PASSED" || echo "MONITORS TEST FAILED"
exit $failed
