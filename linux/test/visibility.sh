#!/usr/bin/env bash
# When widgets are on screen (#361), headless: "start hidden" until the user shows all widgets again (tray), each "show only
# when" rule (time window, on AC / on battery, a monitor), "hide during fullscreen apps" with a real fullscreen X11 window, and
# peek (umbractl peek lifts widgets above windows as UTILITY + ABOVE, then puts them back as DESKTOP windows).
#   linux/test/visibility.sh      Needs what headless.sh needs, plus xdotool, xprop, python3-gi (GTK 4) and gdbus.
set -u
here=$(cd "$(dirname "$0")" && pwd)
command -v xdotool >/dev/null && command -v xprop >/dev/null || { echo "visibility.sh: needs xdotool and xprop" >&2; exit 2; }
work=$(mktemp -d "${UMBRA_TEST_TMP:-${TMPDIR:-/tmp}}/umbra-vis-XXXXXX")
trap 'rm -rf "$work"' EXIT
export HERE="$here"

# One settings.json per case: the edit-widget with the given extra settings (and general settings).
cfg() { # name widget-json general-json
  printf '{"general":{%s},"widgets":{"edit-widget":{"enabled":true%s}}}\n' "$3" "$2" > "$work/$1.json"
}
cfg hidden ',"startHidden":true' ''
now=$(date +%H); later=$(( (10#$now + 2) % 24 )); later2=$(( (10#$now + 3) % 24 ))
cfg time ",\"showWhen\":{\"from\":\"$(printf %02d $later):00\",\"to\":\"$(printf %02d $later2):00\"}" ''
cfg battery ',"showWhen":{"onAc":false}' ''
cfg ac ',"showWhen":{"onAc":true}' ''
cfg monitor ',"showWhen":{"monitor":"NOPE-9"}' ''
cfg full '' '"hideOnFullscreen":true'
cfg plain '' ''

common='
  set -u
  fail() { echo "FAIL  $*"; tail -5 "$UMBRA_DATA_DIR/umbra.log"; exit 1; }
  ok() { echo "ok    $*"; }
  shown() { xdotool search --onlyvisible --name "^Umbra - Edit widget" >/dev/null 2>&1; }
  win() { xdotool search --name "^Umbra - Edit widget" | head -1; }
'
run() { # case script
  "$here/headless.sh" -w "$here/edit-widget" -j "$work/$1.json" -s 5 -- bash -c "$common$2" || exit 1
}

# 1. Start hidden: no window on screen until "Hide widgets" is toggled off and on in the tray (= show all again).
run hidden '
  shown && fail "a start-hidden widget is on screen"
  ok "start hidden: not shown at start"
  reg="$UMBRA_DATA_DIR/watcher.txt"; : > "$reg"
  python3 "$HERE/fake-sni-watcher.py" "$reg" & wpid=$!
  for _ in $(seq 1 50); do [ -s "$reg" ] && break; sleep 0.2; done
  read -r _ service < "$reg" || fail "no tray"
  click() {
    layout=$(gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.GetLayout -- 0 -1 "[]")
    id=$(echo "$layout" | grep -oP "\(\K[0-9]+(?=, \{[^}]*.label.: <.$1)" | head -1)
    gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.Event "$id" clicked "<0>" 0 >/dev/null; sleep 1
  }
  click "Hide widgets"; click "Hide widgets"
  kill $wpid 2>/dev/null
  shown || fail "showing all widgets again did not reveal the start-hidden one"
  ok "start hidden: shown after Hide widgets off again"
'

# 2. Show-when rules.
run time '
  shown && fail "a widget outside its time window is on screen"
  ok "show when: hidden outside its time window"
'
run battery '
  shown && fail "an on-battery-only widget is on screen on mains power (no UPower = mains)"
  ok "show when: on-battery-only hidden on mains power"
'
run ac '
  shown || fail "an AC-only widget is hidden on mains power"
  ok "show when: AC-only shown on mains power"
'
run monitor '
  shown && fail "a widget for a missing monitor is on screen"
  ok "show when: hidden while its monitor is not connected"
'

# 3. Hide during fullscreen apps: a real fullscreen GTK window becomes the active window.
run full '
  shown || fail "the widget is not on screen before the fullscreen app"
  python3 -I -c "
import gi; gi.require_version(\"Gtk\", \"4.0\"); from gi.repository import Gtk, GLib
app = Gtk.Application(application_id=\"test.fullscreen\")
def go(a):
    w = Gtk.ApplicationWindow(application=a, title=\"Fullscreen test\"); w.present(); w.fullscreen()
    GLib.timeout_add(4000, a.quit)
app.connect(\"activate\", go); app.run([])" & fpid=$!
  sleep 2.5
  $UMBRACTL status | grep -q "\"hiddenByFullscreen\": true" || fail "status does not say hiddenByFullscreen: $($UMBRACTL status | grep -i fullscreen)"
  shown && fail "the widget stayed on screen over a fullscreen app"
  ok "fullscreen: widgets hidden while a fullscreen app is active"
  wait $fpid; sleep 1.5
  shown || fail "the widget did not come back after the fullscreen app closed"
  ok "fullscreen: widgets back when it closes"
'

# 4. Peek: UTILITY + ABOVE for the given seconds, then DESKTOP again; the widget never gets the focus.
run plain '
  w=$(win); [ -n "$w" ] || fail "no widget window"
  type() { xprop -id "$(win)" _NET_WM_WINDOW_TYPE 2>/dev/null; }
  type | grep -q DESKTOP || fail "not a DESKTOP window before peek: $(type)"
  out=$($UMBRACTL peek 2) || fail "umbractl peek failed: $out"
  echo "$out" | grep -q "\"peeking\": true" || fail "peek did not start: $out"
  sleep 0.8
  type | grep -q UTILITY || fail "not lifted during peek: $(type)"
  xprop -id "$(win)" _NET_WM_STATE | grep -q ABOVE || fail "no ABOVE state during peek: $(xprop -id "$(win)" _NET_WM_STATE)"
  [ "$(xdotool getwindowfocus 2>/dev/null)" = "$(win)" ] && fail "peek gave the widget the focus"
  ok "peek: widgets above windows, without focus"
  sleep 2.5
  type | grep -q DESKTOP || fail "not back on the desktop after peek: $(type)"
  $UMBRACTL status | grep -q "\"peeking\": false" || true
  ok "peek: back on the desktop after the delay"
  kill -0 "$UMBRA_PID" || fail "umbra died"
'
echo "VISIBILITY TEST PASSED"
