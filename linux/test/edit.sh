#!/usr/bin/env bash
# Edit mode and focus gating on X11 (#183), headless with xdotool as the "real" mouse and keyboard:
#  - a page that asks for drag, resize, zoom, nudge and focus on its own gets none of them;
#  - a real press-and-drag moves the widget and the new position is saved; Ctrl+wheel zooms; plain wheel does not;
#  - typing reaches a widget only after a real click on it; the pill's buttons flip snapping and end edit mode.
#
#   dotnet build Umbra.Linux.slnf && linux/test/edit.sh
set -u
here=$(cd "$(dirname "$0")" && pwd)
if [ "${UMBRA_PID:-}" = "" ]; then
  command -v xdotool >/dev/null || { echo "edit.sh: xdotool is not installed" >&2; exit 2; }
  json=$(mktemp); printf '{"general":{"logLevel":"verbose","lockAll":false,"snapEnabled":false}}' > "$json"
  "$here/headless.sh" -w "$here/edit-widget" -j "$json" -s 3 -- "$0"; code=$?
  rm -f "$json"; exit $code
fi

# ----- inside the session -----
failed=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failed=1; fi; }
log="$UMBRA_DATA_DIR/umbra.log"
data="$UMBRA_DATA_DIR/data/edit-widget.json"
window() { xdotool search --name "Umbra - Edit widget" | head -1; }
geometry() { xwininfo -id "$1" | awk '/Absolute upper-left X/ {x=$4} /Absolute upper-left Y/ {y=$4} /Width:/ {w=$2} /Height:/ {h=$2} END {print x","y" "w"x"h}'; }
field() { $UMBRACTL list | python3 -c 'import json,sys; w=[w for w in json.load(sys.stdin) if w["id"]=="edit-widget"][0]; print(w.get(sys.argv[1]))' "$1"; }
focused() { xdotool getwindowfocus 2>/dev/null; }

for _ in $(seq 1 40); do grep -q '"attacked"' "$data" 2>/dev/null && break; sleep 0.5; done
w=$(window)
check 'edit mode is on and the pill is shown' '[ -n "$(xdotool search --name "Umbra - editing widgets" | head -1)" ]'
start=$(geometry "$w")
check "the page asked for drag, resize, zoom and nudge by itself: the widget did not move or change size ($start)" '[ "$start" = "400,300 255x136" ]'
check 'the resize request without a press is in the log' 'grep -q "resize ignored: no mouse press" "$log"'
check 'the zoom request without Ctrl+wheel is in the log' 'grep -q "zoom ignored: no Ctrl+wheel" "$log"'
check 'the page asked for the focus by itself and did not get it' '[ "$(focused)" != "$w" ]'
check 'the page asked to leave edit mode by itself and edit mode is still on' '$UMBRACTL edit-mode | grep -q "\"editMode\": true"'
xdotool type --delay 20 "stolen"; sleep 0.5
check 'typing without a click does not reach the widget' '! grep -q "stolen" "$data"'

# A real drag: press in the widget (away from the text field), move 180 right and 90 down, release.
eval "$(xdotool getwindowgeometry --shell "$w")"
xdotool mousemove $((X + 190)) $((Y + 10)); sleep 0.3
xdotool mousemove $((X + 200)) $((Y + 12)); sleep 0.3
xdotool mousedown 1; sleep 0.2
for i in $(seq 1 18); do xdotool mousemove_relative 10 5; sleep 0.03; done
sleep 0.3; xdotool mouseup 1; sleep 0.8
moved=$(geometry "$w")
# The page reports the drag after the first few pixels, so the window follows from there: 17 of the 18 steps of 10,5.
check "a real drag moved the widget by 170,85 (now $moved)" '[ "$moved" = "570,385 255x136" ]'
check "the new position is saved ($(grep -o '"x": *[0-9]*, *"y": *[0-9]*' "$UMBRA_DATA_DIR/settings.json" | head -1))" 'tr -d " \n" < "$UMBRA_DATA_DIR/settings.json" | grep -q "\"x\":570,\"y\":385"'

# Wheel: plain does nothing, with Ctrl it zooms one step per notch.
eval "$(xdotool getwindowgeometry --shell "$w")"
xdotool mousemove $((X + 200)) $((Y + 12))
before=$(field scale)
xdotool click 4; sleep 0.6
check "a plain wheel turn does not zoom (scale $(field scale))" '[ "$(field scale)" = "$before" ]'
xdotool keydown ctrl; sleep 0.2; xdotool click --repeat 4 --delay 40 4; sleep 0.3; xdotool keyup ctrl; sleep 1
after=$(field scale)
check "Ctrl+wheel zooms in (scale $before -> $after)" 'python3 -c "import sys; sys.exit(0 if float(sys.argv[2]) > float(sys.argv[1]) else 1)" "$before" "$after"'
check "the window grew with it ($(geometry "$w"))" '[ "$(geometry "$w" | cut -d" " -f2)" != "255x136" ]'

# In edit mode a click selects the widget and lends it the keyboard for the arrow keys.
eval "$(xdotool getwindowgeometry --shell "$w")"
xdotool mousemove $((X + 200)) $((Y + 12)); sleep 0.2; xdotool click 1; sleep 0.8
check 'after a real click the widget has the keyboard focus' '[ "$(focused)" = "$w" ]'

# Arrow key nudge of the selected widget.
before=$(geometry "$w")
xdotool key Right; sleep 0.2; xdotool key Right; sleep 0.8
check "arrow keys nudge the selected widget ($before -> $(geometry "$w"))" '[ "$(geometry "$w")" != "$before" ]'

# The pill: Snap flips the setting, Done ends edit mode. Neither takes the focus.
pill=$(xdotool search --name "Umbra - editing widgets" | head -1)
eval "$(xdotool getwindowgeometry --shell "$pill")"
snap_before=$($UMBRACTL status | grep -o '"snapEnabled": [a-z]*')
xdotool mousemove $((X + 160)) $((Y + HEIGHT / 2)) click 1; sleep 0.8
check "the pill's Snap button flips snapping ($snap_before -> $($UMBRACTL status | grep -o '"snapEnabled": [a-z]*'))" '[ "$($UMBRACTL status | grep -o "\"snapEnabled\": [a-z]*")" != "$snap_before" ]'
check 'a click on the pill does not take the focus' '[ "$(focused)" != "$pill" ]'
xdotool mousemove $((X + WIDTH - 30)) $((Y + HEIGHT / 2)) click 1; sleep 1
check "the pill's Done button ends edit mode" '$UMBRACTL edit-mode | grep -q "\"editMode\": false"'
check 'the pill is gone' '[ -z "$(xdotool search --onlyvisible --name "Umbra - editing widgets" | head -1)" ]'

# Typing after a real click on the text field (widgets locked again: fields are usable).
eval "$(xdotool getwindowgeometry --shell "$w")"
xdotool mousemove $((X + WIDTH * 30 / 100)) $((Y + HEIGHT * 34 / 100)); sleep 0.2; xdotool click 1; sleep 0.8
xdotool type --delay 30 "hello"; sleep 0.8
check "typing after the click reaches the widget ($(grep -o '"typed":"[^"]*"' "$data"))" 'grep -q "\"typed\":\"hello\"" "$data"'

# Locked again: a real drag must not move anything.
before=$(geometry "$w")
eval "$(xdotool getwindowgeometry --shell "$w")"
xdotool mousemove $((X + 200)) $((Y + 12)) mousedown 1
for i in $(seq 1 10); do xdotool mousemove_relative 10 5; sleep 0.02; done
xdotool mouseup 1; sleep 0.6
check "locked widgets do not move ($(geometry "$w"))" '[ "$(geometry "$w")" = "$before" ]'

[ $failed = 0 ] && echo "EDIT TEST PASSED" || echo "EDIT TEST FAILED"
exit $failed
