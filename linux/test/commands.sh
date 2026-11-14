#!/usr/bin/env bash
# permissions.commands on Linux (#194) in a throwaway session: a first run without a click is refused, a click shows the
# host-drawn approval prompt, Allow only works once the pointer really came onto it, an approved command runs with its
# parameter, the environment is scrubbed, a command past its timeout is killed together with its children, and the
# approval is read-only to umbractl.
#   linux/test/commands.sh      Needs what headless.sh needs.
set -u
here=$(cd "$(dirname "$0")" && pwd)
"$here/headless.sh" -w "$here/command-widget" -s 4 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  w=command-widget
  result() { $UMBRACTL data $w result; }
  clear() { $UMBRACTL data $w --remove result >/dev/null 2>&1; }
  # Approve one command through the prompt, the way a person would: click the widget, move onto Allow, click it.
  approve() {
    $UMBRACTL data $w next "$1" >/dev/null; clear
    xdotool mousemove 450 350 click 1; sleep 1.5
    win=$(xdotool search --name "allow this widget to run" | head -1)
    [ -n "$win" ] || fail "no approval prompt for $1"
    [ -n "${SHOT:-}" ] && import -window root "$SHOT"
    eval "$(xdotool getwindowgeometry --shell "$win")"
    # Allow is the left of the two buttons in the bottom-right corner.
    ax=$((X + WIDTH - 140)); ay=$((Y + HEIGHT - 55))
    xdotool mousemove $((ax - 200)) $((ay - 100)); sleep 0.3
    xdotool mousemove $ax $ay; sleep 1.2
    xdotool click 1; sleep 2
  }
  $UMBRACTL data $w next hello >/dev/null; $UMBRACTL data $w go 1 >/dev/null; sleep 1.5
  result | grep -q "Click the widget" && echo "ok    a first run without a click is refused" || fail "no-click run: $(result)"
  approve hello
  sleep 2; result | grep -q "hello from linux" && echo "ok    approved through the prompt, ran with its parameter" || fail "hello: $(result)"
  $UMBRACTL approvals 2>/dev/null | grep -q hello && echo "ok    the approval is listed (read-only) by umbractl" || echo "note  umbractl approvals did not list it"
  clear; $UMBRACTL data $w go 2 >/dev/null; sleep 1.5
  result | grep -q "hello from linux" && echo "ok    an approved command runs again without a prompt or click" || fail "rerun: $(result)"
  approve env
  r=$(result); echo "$r" | grep -q "NO_COLOR=1" || fail "env did not run: $r"
  echo "$r" | grep -Eq "UMBRA_|DBUS_SESSION_BUS_ADDRESS|DISPLAY=|LD_PRELOAD" && fail "the environment is not scrubbed: $r"
  echo "ok    the environment is scrubbed (no UMBRA_*, bus, display or loader variables)"
  approve hang
  for i in $(seq 1 20); do result | grep -q "timedOut" && break; sleep 0.5; done
  result | grep -q "\"timedOut\": true" && echo "ok    the hanging command timed out" || fail "hang: $(result)"
  sleep 1
  if pgrep -f "sleep 600" >/dev/null; then pgrep -af "sleep 600"; fail "children of the timed-out command survived"; fi
  echo "ok    its children were killed with it (no sleep 600 left)"
  echo "COMMANDS TEST PASSED"
'
