#!/usr/bin/env bash
# The tray (#191) in a throwaway session: without a watcher Umbra starts and logs that there is no tray; with a (fake)
# StatusNotifierWatcher it registers a StatusNotifierItem whose properties and dbusmenu layout are read back over D-Bus,
# and clicking menu items (dbusmenu Event) toggles Arrange and opens Settings.
#   linux/test/tray.sh      Needs what headless.sh needs, plus python3-gi and gdbus.
set -u
here=$(cd "$(dirname "$0")" && pwd)

# 1. No watcher: Umbra runs on and says why there is no icon.
"$here/headless.sh" -w "$here/edit-widget" -s 5 -- bash -c '
  $UMBRACTL status >/dev/null || { echo "FAIL umbra does not answer without a watcher"; exit 1; }
  grep -q "No StatusNotifierWatcher" "$UMBRA_DATA_DIR/umbra.log" && echo "ok    no watcher: logged, Umbra still answers" || { echo "FAIL no log line about the missing watcher"; exit 1; }
' || exit 1

# 2. With a watcher (started before Umbra by the session, so the registration happens at startup or when it appears).
"$here/headless.sh" -w "$here/edit-widget" -s 5 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  reg="$UMBRA_DATA_DIR/watcher.txt"; : > "$reg"
  python3 '"$here"'/fake-sni-watcher.py "$reg" & wpid=$!
  for i in $(seq 1 50); do [ -s "$reg" ] && break; sleep 0.2; done
  [ -s "$reg" ] || { kill $wpid; fail "Umbra did not register with a watcher that appeared after it started"; }
  read -r sender service < "$reg"
  echo "ok    registered $service from $sender"
  prop() { gdbus call --session -d "$service" -o /StatusNotifierItem -m org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierItem "$1"; }
  [ "$(prop Id)" = "(<'"'"'io.github.aminechr54.Umbra'"'"'>,)" ] && echo "ok    Id is the app id" || fail "Id: $(prop Id)"
  prop IconName | grep -q "io.github.aminechr54.Umbra" && echo "ok    IconName $(prop IconName)" || fail "IconName: $(prop IconName)"
  prop IconThemePath | grep -q "/icons" && echo "ok    IconThemePath $(prop IconThemePath)" || fail "IconThemePath"
  prop ToolTip | grep -q "widget" && echo "ok    tooltip $(prop ToolTip)" || fail "ToolTip: $(prop ToolTip)"
  layout=$(gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.GetLayout -- 0 -1 "[]")
  for label in "Settings..." "Arrange widgets (unlock)" "Hide widgets" "Layouts" "Save current layout" "Refresh all" "Restart Umbra" "Quit"; do
    echo "$layout" | grep -qF "$label" || fail "menu has no \"$label\": $layout"
  done
  echo "ok    the menu has the Windows items"
  echo "$layout" | grep -qF "Edit widget" && echo "ok    the widget is listed with a checkmark" || fail "widget missing from the menu"
  # The id of an item by its label (the layout is (u (ia{sv}av)) text).
  idof() { echo "$layout" | grep -oP "\(\K[0-9]+(?=, \{[^}]*'"'"'label'"'"': <'"'"'$1)" | head -1; }
  arrange=$(idof "Arrange widgets")
  [ -n "$arrange" ] || fail "no id for Arrange"
  gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.Event "$arrange" clicked "<0>" 0 >/dev/null
  sleep 1
  $UMBRACTL status | grep -q "\"editMode\": true" && echo "ok    clicking Arrange turned edit mode on" || fail "Arrange click did nothing: $($UMBRACTL status)"
  layout=$(gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.GetLayout -- 0 -1 "[]")
  echo "$layout" | grep -qP "Arrange widgets \(unlock\)'"'"'>, '"'"'toggle-type'"'"': <'"'"'checkmark'"'"'>, '"'"'toggle-state'"'"': <1>" \
    && echo "ok    the menu shows Arrange checked after the change" || fail "Arrange not checked: $layout"
  gdbus call --session -d "$service" -o /StatusNotifierItem -m org.kde.StatusNotifierItem.Activate 0 0 >/dev/null
  sleep 2
  xdotool search --name "^Umbra$" >/dev/null && echo "ok    a left click (Activate) opens Settings" || fail "Activate did not open the Settings window"
  kill $wpid
  echo "TRAY TEST PASSED"
'
