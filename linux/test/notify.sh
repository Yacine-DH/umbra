#!/usr/bin/env bash
# umbra.notify on Linux (#189) in a throwaway session with a fake notification server: refused while the user's switch is
# off, shown once it is on (title, escaped body, buttons, url, app id hint), a tag replaces the earlier one, a button click
# reaches the widget, the body click opens the url, and the rate limit (5 per 10 minutes) holds.
#   linux/test/notify.sh      Needs what headless.sh needs, plus python3-gi.
set -u
here=$(cd "$(dirname "$0")" && pwd)
"$here/headless.sh" -w "$here/notify-widget" -s 4 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  log="$UMBRA_DATA_DIR/notes.jsonl"; : > "$log"
  python3 '"$here"'/fake-notifications.py "$log" & npid=$!
  trap "kill $npid 2>/dev/null" EXIT
  sleep 1
  w=notify-widget
  go() { $UMBRACTL data $w go "$1" >/dev/null; for i in $(seq 1 30); do r=$($UMBRACTL data $w result 2>/dev/null); echo "$r" | grep -q " $1" && break; sleep 0.2; done; echo "$r"; }
  $UMBRACTL data $w ask 1 >/dev/null; sleep 1
  $UMBRACTL data $w prompt | grep -q "Click the widget first" && echo "ok    the permission prompt is refused without a click" || fail "prompt without click: $($UMBRACTL data $w prompt)"
  r=$(go 1); echo "$r" | grep -q "error 1: Notifications are off" && echo "ok    refused while the switch is off" || fail "expected a refusal, got $r"
  [ -s "$log" ] && fail "a notification was shown while off"
  $UMBRACTL configure $w --notifications on >/dev/null
  r=$(go 2); echo "$r" | grep -q "ok 2" || fail "notify with the switch on: $r"
  sleep 1
  line=$(tail -1 "$log"); echo "      $line"
  echo "$line" | grep -q "\"summary\": \"Hello <b>2</b>\"" && echo "ok    title as sent (summary is plain text)" || fail "summary"
  echo "$line" | grep -q "Body &amp; &lt;i&gt;markup&lt;/i&gt;" && echo "ok    body markup escaped" || fail "body not escaped"
  echo "$line" | grep -q "\"actions\": \[\"default\", \"Open\", \"a:snooze\", \"Snooze\", \"a:done\", \"Done\"\]" && echo "ok    buttons and the body click" || fail "actions"
  echo "$line" | grep -q "io.github.aminechr54.Umbra" && echo "ok    desktop-entry hint" || fail "hint"
  id=$(echo "$line" | python3 -c "import json,sys; print(json.load(sys.stdin)[\"id\"])")
  r=$(go 3); sleep 1
  tail -1 "$log" | grep -q "\"replaces\": $id" && echo "ok    the same tag replaces the earlier notification" || fail "tag did not replace: $(tail -1 "$log")"
  echo "$id a:snooze" > "$log.click"; sleep 1.5
  $UMBRACTL data $w action | grep -q "snooze|t1" && echo "ok    a button click reaches the widget" || fail "action not delivered: $($UMBRACTL data $w action)"
  echo "$id default" > "$log.click"; sleep 1.5
  grep -q "https://example.com/x" "$UMBRA_OPENED" && echo "ok    the body click opens the url" || fail "url not opened"
  go 4 >/dev/null; go 5 >/dev/null; go 6 >/dev/null # 2-6: five shown
  r=$(go 7); echo "$r" | grep -q "Rate limit" && echo "ok    the 6th in 10 minutes is refused ($r)" || fail "rate limit: $r"
  # The prompt after a real click: a host window appears; Esc answers "Not now" (no change), the widget hears false.
  $UMBRACTL configure $w --notifications default >/dev/null
  xdotool mousemove 450 350 click 1; sleep 1.5
  xdotool search --name "^Umbra$" >/dev/null && echo "ok    a real click on the widget shows the host prompt" || fail "no prompt window after a click"
  import -window root "$UMBRA_DATA_DIR/prompt.png" 2>/dev/null; [ -n "${NOTIFY_SHOT:-}" ] && cp "$UMBRA_DATA_DIR/prompt.png" "$NOTIFY_SHOT"
  xdotool key Escape; sleep 1
  $UMBRACTL data $w prompt | grep -q "answer false" && echo "ok    Esc means Not now: the widget hears false" || fail "after Esc: $($UMBRACTL data $w prompt)"
  echo "NOTIFY TEST PASSED"
'
