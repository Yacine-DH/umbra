#!/usr/bin/env bash
# The host-drawn widget menu (#360), headless with xdotool as the real mouse and keyboard: a page firing synthetic contextmenu
# events never opens it; a right press does, Esc closes it; Refresh, Options..., Arrange widgets, New group, Add another copy,
# Remove copy (+ Undo from the tray menu, data back) and Hide each do what they say.
#   linux/test/menu.sh [shot-dir]     Needs what headless.sh needs, plus xdotool, python3-gi and gdbus (the fake tray watcher).
# With shot-dir, a screenshot of this session's own Xvfb screen (never the real desktop) with the menu open is saved there.
set -u
here=$(cd "$(dirname "$0")" && pwd)
command -v xdotool >/dev/null || { echo "menu.sh: xdotool is not installed" >&2; exit 2; }
export SHOTS=${1:-} HERE="$here"
[ -n "$SHOTS" ] && mkdir -p "$SHOTS"

"$here/headless.sh" -w "$here/menu-widget" -s 5 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; tail -5 "$UMBRA_DATA_DIR/umbra.log"; exit 1; }
  ok() { echo "ok    $*"; }
  opened() { grep -c "\[$1\] widget menu opened" "$UMBRA_DATA_DIR/umbra.log"; }
  win() { xdotool search --name "^Umbra - $1\$" | head -1; }
  # A right click on a widget window (title), then wait for the menu to be logged.
  menu() { # title instance
    local w; w=$(win "$1"); [ -n "$w" ] || fail "no window \"$1\""
    eval "$(xdotool getwindowgeometry --shell "$w")"
    local before; before=$(opened "$2")
    # A new copy opens a little below and right of the first one: click the copy near its bottom-right corner, the first near its top-left.
    case "$2" in *"#"*) xdotool mousemove $((X + WIDTH - 15)) $((Y + HEIGHT - 15));; *) xdotool mousemove $((X + 15)) $((Y + 15));; esac
    sleep 0.2; xdotool click 3
    for _ in $(seq 1 20); do [ "$(opened "$2")" -gt "$before" ] && { sleep 0.4; return; }; sleep 0.2; done
    fail "a right click on \"$1\" opened no menu"
  }
  # The n-th item below Refresh (0 = Refresh), chosen with the keyboard like a user would.
  pick() { menu "$1" "$2"; for _ in $(seq 1 "$3"); do xdotool key Down; sleep 0.1; done; xdotool key Return; sleep 1.2; }
  json() { $UMBRACTL list; }
  inst_enabled() { json | python3 -I -c "import json,sys; d=json.load(sys.stdin); w=[x for x in (d if isinstance(d,list) else d.get(\"widgets\",[])) if x.get(\"id\")==sys.argv[1]]; print(w[0].get(\"enabled\") if w else \"absent\")" "$1"; }

  for _ in $(seq 1 40); do [ -n "$(win "Menu widget \(menu test\)")" ] && break; sleep 0.25; done
  W="Menu widget \(menu test\)"; W2="Menu widget \(menu test\) \(2\)"

  # 1. The page alone: synthetic contextmenu events every 300 ms for 3 s open nothing.
  sleep 3
  [ "$(opened menu-widget)" = 0 ] || fail "a page-made contextmenu event opened the host menu"
  ok "synthetic contextmenu events from the page open no menu"

  # 2. A real right click opens it; Esc closes it and runs nothing.
  menu "$W" menu-widget
  [ -n "$SHOTS" ] && import -window root "$SHOTS/menu.png"
  pop=$(xdotool search --onlyvisible --name "^umbra\$" | tail -1)
  [ -n "$pop" ] || fail "no menu window is mapped"
  xdotool key Escape; sleep 0.8
  xdotool search --onlyvisible --name "^umbra\$" | grep -qx "$pop" && fail "Esc did not close the menu"
  ok "a right click opens the host menu, Esc closes it"

  # 3. Refresh reloads the page.
  pick "$W" menu-widget 0
  ok "Refresh runs (no error)"

  # 4. Arrange widgets turns edit mode on (and the checkmark comes back on).
  pick "$W" menu-widget 2
  $UMBRACTL status | grep -q "\"editMode\": true" || fail "Arrange widgets did not turn edit mode on"
  $UMBRACTL edit-mode off >/dev/null; sleep 1 # windows are re-stacked as edit mode ends
  ok "Arrange widgets turns edit mode on"

  # 4b. In edit mode a right press opens the menu, the page gets no keyboard focus from it, and Esc closes the menu
  #     instead of ending edit mode (review of #395).
  $UMBRACTL edit-mode on >/dev/null; sleep 1
  menu "$W" menu-widget
  xdotool key Escape; sleep 0.8
  xdotool search --onlyvisible --name "^umbra\$" >/dev/null 2>&1 && fail "Esc did not close the menu in edit mode"
  $UMBRACTL status | grep -q "\"editMode\": true" || fail "Esc with the menu open ended edit mode"
  [ "$(xdotool getwindowfocus 2>/dev/null)" = "$(win "$W")" ] && fail "the right press gave the widget keyboard focus"
  $UMBRACTL edit-mode off >/dev/null; sleep 1
  ok "edit mode: right press opens the menu without focus, Esc closes only the menu"

  # 5. Add to tab group > New group makes a group of this widget.
  menu "$W" menu-widget; for _ in 1 2 3; do xdotool key Down; sleep 0.1; done; xdotool key Return; sleep 0.6; xdotool key Down; sleep 0.1; xdotool key Return; sleep 1.2 # the submenu opens on its "back" row
  $UMBRACTL groups | grep -q "menu-widget" || fail "New group did not make a group: $($UMBRACTL groups)"
  $UMBRACTL groups leave menu-widget >/dev/null; sleep 1
  ok "Add to tab group > New group groups the widget"

  # 6. Add another copy.
  pick "$W" menu-widget 5
  for _ in $(seq 1 20); do [ -n "$(win "$W2")" ] && break; sleep 0.25; done
  [ -n "$(win "$W2")" ] || fail "Add another copy made no second window"
  ok "Add another copy opens menu-widget#2"

  # 7. Remove copy (only on the copy), then Undo from the tray menu brings it back with its data.
  sleep 1.5 # the copy stores its value
  data="$UMBRA_DATA_DIR/data/menu-widget__2.json"
  [ -f "$data" ] || fail "the copy stored nothing ($data)"
  born=$(cat "$data")
  reg="$UMBRA_DATA_DIR/watcher.txt"; : > "$reg"
  python3 "$HERE/fake-sni-watcher.py" "$reg" & wpid=$!
  for _ in $(seq 1 50); do [ -s "$reg" ] && break; sleep 0.2; done
  read -r _ service < "$reg" || fail "no tray registration"
  pick "$W2" "menu-widget#2" 6
  [ -z "$(win "$W2")" ] || fail "Remove copy left the window"
  [ "$(inst_enabled "menu-widget#2")" = absent ] || fail "menu-widget#2 still listed after Remove copy"
  layout=$(gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.GetLayout -- 0 -1 "[]")
  echo "$layout" | grep -q "Undo remove Menu widget (menu test) (2)" || fail "no Undo item in the tray menu"
  id=$(echo "$layout" | grep -oP "\(\K[0-9]+(?=, \{[^}]*.label.: <.Undo remove)" | head -1)
  gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.Event "$id" clicked "<0>" 0 >/dev/null
  for _ in $(seq 1 20); do [ -n "$(win "$W2")" ] && break; sleep 0.25; done
  [ -n "$(win "$W2")" ] || fail "Undo did not bring the copy back"
  [ "$(cat "$data" 2>/dev/null)" = "$born" ] || fail "Undo did not restore the copy data"
  layout=$(gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.GetLayout -- 0 -1 "[]")
  echo "$layout" | grep -q "Undo remove" && fail "the Undo item stayed after Undo"
  kill $wpid 2>/dev/null
  ok "Remove copy + tray Undo restore the copy with its data"

  # 8. Hide turns the copy off.
  pick "$W2" "menu-widget#2" 4
  [ "$(inst_enabled "menu-widget#2")" = False ] || fail "Hide did not turn menu-widget#2 off: $(inst_enabled "menu-widget#2")"
  ok "Hide turns the widget off"
  # 9. Options... opens Settings (last: its window covers the widgets).
  pick "$W" menu-widget 1
  for _ in $(seq 1 40); do xdotool search --onlyvisible --name "^Umbra\$" >/dev/null && break; sleep 0.25; done
  xdotool search --onlyvisible --name "^Umbra\$" >/dev/null || fail "Options... opened no Settings window"
  grep -q "Could not open options" "$UMBRA_DATA_DIR/umbra.log" && fail "Options... failed"
  ok "Options... opens Settings"

  kill -0 "$UMBRA_PID" || fail "umbra died"
  echo "MENU TEST PASSED"
'
