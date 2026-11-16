#!/usr/bin/env bash
# Session signals on Linux (#193) in a throwaway session: the screen lock pauses widgets and unlocking resumes them; a
# desktop accent change (portal SettingChanged) reaches widgets live when the user follows the system accent; GNOME's 12/24-hour
# switch (portal clock-format) beats LANG and reaches widgets live (#359).
# Sleep/resume and clock or time-zone changes need root and are covered by unit tests plus a human check.
#   linux/test/session.sh      Needs what headless.sh needs, plus gdbus.
set -u
here=$(cd "$(dirname "$0")" && pwd)
json=$(mktemp); trap 'rm -f "$json"' EXIT
echo '{"general":{"accent":"windows"}}' > "$json"
# en_US is a 12-hour locale; the GNOME switch overrides it either way.
unset LC_ALL LC_TIME; export LANG=en_US.UTF-8
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
  # The start value can come from the real GNOME setting (the private bus may activate xdg-desktop-portal), so only the
  # locale is checked there; the switch is driven both ways.
  seen | grep -q "\"locale\": \"en-US\"" && echo "ok    LANG=en_US: locale en-US" || fail "start locale: $(seen)"
  for f in 12h 24h 12h; do
    gdbus emit --session --object-path /org/freedesktop/portal/desktop --signal org.freedesktop.portal.Settings.SettingChanged \
      org.gnome.desktop.interface clock-format "<\"$f\">" >/dev/null; sleep 1
    want=$([ $f = 12h ] && echo true || echo false)
    seen | grep -q "\"hour12\": $want" && echo "ok    GNOME clock-format $f: widgets get hour12 $want" || fail "$f: $(seen)"
  done
  grep -q "Session locked" "$UMBRA_DATA_DIR/umbra.log" && echo "ok    logged" || fail "no log line"
  echo "SESSION TEST PASSED"
'
