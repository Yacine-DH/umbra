#!/usr/bin/env bash
# Session signals on Linux (#193) in a throwaway session: the screen lock pauses widgets and unlocking resumes them; a
# desktop accent change (portal SettingChanged) reaches widgets live when the user follows the system accent.
# Sleep/resume and clock or time-zone changes need root and are covered by unit tests plus a human check.
#   linux/test/session.sh      Needs what headless.sh needs, plus gdbus.
set -u
here=$(cd "$(dirname "$0")" && pwd)
json=$(mktemp); trap 'rm -f "$json"' EXIT
echo '{"general":{"accent":"windows"}}' > "$json"
"$here/headless.sh" -w "$here/session-widget" -j "$json" -s 4 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  w=session-widget
  seen() { $UMBRACTL data $w seen; }
  seen | grep -q "\"paused\": false" && echo "ok    not paused at start" || fail "start: $(seen)"
  gdbus emit --session --object-path /org/gnome/ScreenSaver --signal org.gnome.ScreenSaver.ActiveChanged true >/dev/null; sleep 1
  seen | grep -q "\"paused\": true" && echo "ok    the screen lock pauses widgets" || fail "lock: $(seen)"
  gdbus emit --session --object-path /org/gnome/ScreenSaver --signal org.gnome.ScreenSaver.ActiveChanged false >/dev/null; sleep 1
  seen | grep -q "\"paused\": false" && echo "ok    unlocking resumes them" || fail "unlock: $(seen)"
  gdbus emit --session --object-path /org/freedesktop/portal/desktop --signal org.freedesktop.portal.Settings.SettingChanged \
    org.freedesktop.appearance accent-color "<(0.2078, 0.5176, 0.8941)>" >/dev/null; sleep 1
  seen | grep -q "\"accent\": \"#3584e4\"" && echo "ok    a desktop accent change reaches the widget ($(seen | grep -o "#[0-9a-f]*"))" || fail "accent: $(seen)"
  gdbus emit --session --object-path /org/freedesktop/portal/desktop --signal org.freedesktop.portal.Settings.SettingChanged \
    org.freedesktop.appearance accent-color "<(0.1, 0.1, 0.2)>" >/dev/null; sleep 1
  a=$(seen | grep -o "#[0-9a-f]\{6\}"); [ -n "$a" ] && [ "$a" != "#1a1a33" ] && echo "ok    a dark accent is lightened to stay readable ($a)" || fail "dark accent: $(seen)"
  grep -q "Session locked" "$UMBRA_DATA_DIR/umbra.log" && echo "ok    logged" || fail "no log line"
  echo "SESSION TEST PASSED"
'
