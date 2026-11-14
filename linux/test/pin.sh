#!/usr/bin/env bash
# X11 desktop pinning (#182), headless: widget windows must be DESKTOP-type, on every workspace, out of taskbar and pager,
# unfocusable, at the position their settings say, below a normal window, and back at that position after a restart.
#
#   dotnet build Umbra.Linux.slnf && linux/test/pin.sh
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
if [ "${UMBRA_PID:-}" = "" ]; then
  for tool in xprop xwininfo xdotool; do command -v $tool >/dev/null || { echo "pin.sh: $tool is not installed" >&2; exit 2; }; done
  exec "$here/headless.sh" -w "$repo/widgets/clock" -s 3 -- "$0" "$@"
fi

# ----- inside the session -----
failed=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failed=1; fi; }
window() { xdotool search --name "Umbra - Clock" | head -1; }
position() { xwininfo -id "$1" | awk '/Absolute upper-left X/ {x=$4} /Absolute upper-left Y/ {y=$4} END {print x","y}'; }

check 'status reports the x11 backend' '$UMBRACTL status | grep -q "\"pinBackend\": \"x11\""'
$UMBRACTL configure clock --x 150 --y 200 >/dev/null; sleep 1
w=$(window)
check 'the clock window exists' '[ -n "$w" ]'
props=$(xprop -id "$w" _NET_WM_WINDOW_TYPE _NET_WM_STATE _NET_WM_DESKTOP WM_HINTS)
check 'window type is DESKTOP' 'echo "$props" | grep -q "_NET_WM_WINDOW_TYPE_DESKTOP"'
check 'skipped by the taskbar' 'echo "$props" | grep -q "_NET_WM_STATE_SKIP_TASKBAR"'
check 'skipped by the pager' 'echo "$props" | grep -q "_NET_WM_STATE_SKIP_PAGER"'
check 'on every workspace' 'echo "$props" | grep -q "_NET_WM_DESKTOP(CARDINAL) = 4294967295"'
check 'does not take input focus' 'echo "$props" | grep -q "input focus: False"'
check "placed at 150,200 (is $(position "$w"))" '[ "$(position "$w")" = "150,200" ]'

if command -v xmessage >/dev/null; then
  xmessage -geometry 400x300+100+150 "a normal window" & other=$!
  sleep 1.5
  order=$(xprop -root _NET_CLIENT_LIST_STACKING | sed 's/.*# //; s/,//g')
  first=$(printf '%d' "$(echo $order | awk '{print $1}')")
  check 'the widget is below a normal window' '[ "$first" = "$w" ]'
  check 'the normal window has the focus, not the widget' '[ "$(xdotool getactivewindow)" != "$w" ]'
  kill $other 2>/dev/null
else echo "skip  stacking (xmessage is not installed)"; fi

# Restart: the position must come back from settings.json.
$UMBRACTL quit >/dev/null
for _ in $(seq 1 20); do kill -0 "$UMBRA_PID" 2>/dev/null || break; sleep 0.5; done
"$UMBRA_BIN" >> "$UMBRA_DATA_DIR/stdout.txt" 2>&1 & second=$!
for _ in $(seq 1 40); do [ -n "$(window)" ] && break; sleep 0.5; done
sleep 1
w=$(window)
check "after a restart it is back at 150,200 (is $(position "$w"))" '[ "$(position "$w")" = "150,200" ]'
$UMBRACTL reset-position clock >/dev/null; sleep 1
check "reset-position moves it to its default spot (is $(position "$w"))" '[ "$(position "$w")" != "150,200" ]'
$UMBRACTL quit >/dev/null; wait $second 2>/dev/null

[ $failed = 0 ] && echo "PIN TEST PASSED" || echo "PIN TEST FAILED"
exit $failed
