#!/usr/bin/env bash
# Global hotkeys (#192), headless on X11: umbractl hotkeys reports the x11 backend and the real state, xdotool key presses
# (XTEST, like a real keyboard) run the actions while another window has the focus, holding the keys runs an action once,
# Caps Lock does not matter, a cleared hotkey is ungrabbed, and a Wayland session without the GlobalShortcuts portal
# reports "not supported" instead of failing.
#
#   dotnet build Umbra.Linux.slnf && linux/test/hotkeys.sh
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
if [ "${UMBRA_PID:-}" = "" ]; then
  for tool in xdotool xmessage dbus-daemon; do command -v $tool >/dev/null || { echo "hotkeys.sh: $tool is not installed" >&2; exit 2; }; done
  exec "$here/headless.sh" -w "$repo/widgets/clock" -s 4 -- "$0" "$@"
fi

# ----- inside the session -----
failed=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failed=1; fi; }
hk() { $UMBRACTL hotkeys "$@"; }
field() { tr -d ' \n' | grep -o "\"action\":\"$1\"[^}]*" ; }
edit_mode() { $UMBRACTL edit-mode | tr -d ' \n' | grep -o '"editMode":[a-z]*' | cut -d: -f2; }
hidden() { hk | tr -d ' \n' | grep -o '"widgetsHidden":[a-z]*' | cut -d: -f2; }

check 'reports the x11 backend' 'hk | tr -d " \n" | grep -q "\"backend\":\"x11\",\"supported\":true"'
hk edit-mode Ctrl+Alt+E >/dev/null
hk toggle-widgets Ctrl+Alt+Shift+F9 >/dev/null
check 'edit-mode is active' 'hk | field edit-mode | grep -q "\"status\":\"active\""'
check 'toggle-widgets is active' 'hk | field toggle-widgets | grep -q "\"status\":\"active\""'

# Another app has the focus: the grab still gets the keys, and no widget is activated. (A passive grab on the root window
# needs a focused window below it: with the focus on None X discards the keys. Openbox on a bare Xvfb leaves the focus on
# None after edit mode maps its pill, so the test focuses the other app again after each edit-mode change; a desktop
# session always has a focused window.)
xmessage -geometry 300x200+600+400 "another app" & other=$!
for _ in $(seq 1 20); do app=$(xdotool search --name '^xmessage$' | head -1); [ -n "$app" ] && break; sleep 0.25; done
xdotool windowfocus --sync "$app"
press() { xdotool key "$@"; sleep 0.7; xdotool windowfocus --sync "$app" 2>/dev/null; }
before=$(edit_mode)
press ctrl+alt+e
check "Ctrl+Alt+E toggles edit mode ($before -> $(edit_mode))" '[ "$(edit_mode)" != "$before" ]'
press ctrl+alt+e
check 'pressing it again toggles back' '[ "$(edit_mode)" = "$before" ]'
xdotool keydown ctrl+alt+e; sleep 1.2; xdotool keyup ctrl+alt+e; sleep 0.7  # held: auto-repeat must not re-run it
check 'holding the keys runs the action once' '[ "$(edit_mode)" != "$before" ]'
press ctrl+alt+e
xdotool key Caps_Lock; xdotool key ctrl+alt+shift+F9; sleep 0.7; xdotool key Caps_Lock
check 'the other app keeps the focus' '[ "$(xdotool getwindowfocus)" = "$app" ]'
check 'Ctrl+Alt+Shift+F9 hides the widgets (with Caps Lock on)' '[ "$(hidden)" = true ]'
press ctrl+alt+shift+F9
check 'and shows them again' '[ "$(hidden)" = false ]'
xdotool windowfocus --sync "$app"

# A keyboard layout change (MappingNotify): "e" moves to another key, and the grab follows it.
if command -v xmodmap >/dev/null; then
  xmodmap -e "keycode 26 = q Q" -e "keycode 24 = e E"; sleep 1
  before=$(edit_mode)
  press ctrl+alt+e
  check "after a layout change Ctrl+Alt+E still works ($before -> $(edit_mode))" '[ "$(edit_mode)" != "$before" ]'
  press ctrl+alt+e
  xmodmap -e "keycode 24 = q Q" -e "keycode 26 = e E"; sleep 1
else echo "skip  layout change (xmodmap is not installed)"; fi

hk edit-mode off >/dev/null; sleep 0.3
before=$(edit_mode)
press ctrl+alt+e
check 'a cleared hotkey does nothing' '[ "$(edit_mode)" = "$before" ]'
check 'and reports off' 'hk | field edit-mode | grep -q "\"status\":\"off\""'
check 'nothing failed in the log' '! grep -q "Hotkey .* failed\|not registered" "$UMBRA_DATA_DIR/umbra.log"'

kill $other 2>/dev/null
$UMBRACTL capture settings:hotkeys "$UMBRA_DATA_DIR/hotkeys-x11.png" >/dev/null
[ -n "${UMBRA_SHOTS:-}" ] && cp "$UMBRA_DATA_DIR/hotkeys-x11.png" "$UMBRA_SHOTS/"

# A Wayland session without the GlobalShortcuts portal (GNOME 46): "not supported", no error loop. The session bus of
# headless.sh can start xdg-desktop-portal on demand, so this part runs on a second bus that has no services at all.
$UMBRACTL quit >/dev/null
for _ in $(seq 1 20); do kill -0 "$UMBRA_PID" 2>/dev/null || break; sleep 0.5; done
cat > "$UMBRA_DATA_DIR/bare-bus.conf" <<EOF
<busconfig><type>session</type><listen>unix:dir=$UMBRA_DATA_DIR</listen>
<policy context="default"><allow send_destination="*" eavesdrop="true"/><allow eavesdrop="true"/><allow own="*"/></policy></busconfig>
EOF
bare=$(dbus-daemon --config-file="$UMBRA_DATA_DIR/bare-bus.conf" --print-address --print-pid --fork)
bare_addr=$(echo "$bare" | head -1); bare_pid=$(echo "$bare" | sed -n 2p)
DBUS_SESSION_BUS_ADDRESS=$bare_addr WAYLAND_DISPLAY=umbra-test-none "$UMBRA_BIN" --background >> "$UMBRA_DATA_DIR/stdout.txt" 2>&1 & UMBRA_PID=$!
for _ in $(seq 1 40); do $UMBRACTL status >/dev/null 2>&1 && break; sleep 0.5; done
sleep 1
check 'without the portal it reports not supported' 'hk | tr -d " \n" | grep -q "\"backend\":\"none\",\"supported\":false"'
check 'a set hotkey says why it is not active' 'hk | field toggle-widgets | grep -q "notsupportedonthisdesktop"'
$UMBRACTL capture settings:hotkeys "$UMBRA_DATA_DIR/hotkeys-none.png" >/dev/null
[ -n "${UMBRA_SHOTS:-}" ] && cp "$UMBRA_DATA_DIR/hotkeys-none.png" "$UMBRA_SHOTS/"
check 'one log line, no loop' '[ "$(grep -c "Global hotkeys" "$UMBRA_DATA_DIR/umbra.log")" -le 3 ] && ! grep -q "GlobalShortcuts portal failed" "$UMBRA_DATA_DIR/umbra.log"'

# The GlobalShortcuts portal (KDE Plasma 6, GNOME 48+), played by fake-global-shortcuts.py on the bare bus: the app id is
# registered, the Windows descriptions and preferred triggers are offered, a binding is pending until the "dialog" is
# answered, Activated runs the action, a change binds in a new session, and a cancelled dialog is reported.
restart() {
  $UMBRACTL quit >/dev/null 2>&1
  for _ in $(seq 1 20); do kill -0 "$UMBRA_PID" 2>/dev/null || break; sleep 0.5; done
  DBUS_SESSION_BUS_ADDRESS=$bare_addr WAYLAND_DISPLAY=umbra-test-portal "$UMBRA_BIN" --background >> "$UMBRA_DATA_DIR/stdout.txt" 2>&1 & UMBRA_PID=$!
  for _ in $(seq 1 40); do $UMBRACTL status >/dev/null 2>&1 && break; sleep 0.5; done
  sleep 1
}
portal="$UMBRA_DATA_DIR/portal.jsonl"; : > "$portal"
DBUS_SESSION_BUS_ADDRESS=$bare_addr python3 "$(dirname "$0")/fake-global-shortcuts.py" "$portal" & fake=$!
for _ in $(seq 1 20); do grep -q ready "$portal" && break; sleep 0.25; done
hk reset >/dev/null
restart
check 'reports the portal backend' 'hk | tr -d " \n" | grep -q "\"backend\":\"portal\",\"supported\":true"'
check 'the app id is registered with the portal' 'grep -q "\"Register\", \"app\": \"io.github.aminechr54.Umbra\"" "$portal"'
check 'no hotkeys, no shortcuts session' '! grep -q CreateSession "$portal"'
hk edit-mode Ctrl+Alt+E >/dev/null; sleep 1
check 'BindShortcuts offers the Windows description and CTRL+ALT+e' 'grep BindShortcuts "$portal" | tail -1 | grep -q "\"edit-mode\", {\"description\": \"Arrange widgets (lock or unlock)\", \"preferred_trigger\": \"CTRL+ALT+e\"}"'
check 'pending until the dialog is answered' 'hk | field edit-mode | grep -q "\"status\":\"pending\""'
$UMBRACTL capture settings:hotkeys "$UMBRA_DATA_DIR/hotkeys-portal-pending.png" >/dev/null
echo accept > "$portal.answer"; sleep 1
check 'active once accepted, with the desktop trigger' 'hk | field edit-mode | grep -q "\"status\":\"active\",\"note\":\"Desktopshortcut:PressCTRL+ALT+e\""'
$UMBRACTL capture settings:hotkeys "$UMBRA_DATA_DIR/hotkeys-portal.png" >/dev/null
[ -n "${UMBRA_SHOTS:-}" ] && cp "$UMBRA_DATA_DIR"/hotkeys-portal*.png "$UMBRA_SHOTS/"
before=$(edit_mode)
echo edit-mode > "$portal.activate"; sleep 1
check "Activated runs the action ($before -> $(edit_mode))" '[ "$(edit_mode)" != "$before" ]'
$UMBRACTL edit-mode off >/dev/null
hk toggle-widgets Ctrl+Alt+T >/dev/null; sleep 1
check 'a change closes the old session and binds both in a new one' '[ "$(grep -c CreateSession "$portal")" = 2 ] && grep -q "\"Close\"" "$portal" && grep BindShortcuts "$portal" | tail -1 | grep -q toggle-widgets'
hk toggle-widgets Ctrl+Alt+T >/dev/null; sleep 1
check 'setting the same hotkey again does not bind again' '[ "$(grep -c CreateSession "$portal")" = 2 ]'
echo cancel > "$portal.answer"; sleep 1
check 'a cancelled dialog is reported' 'hk | field toggle-widgets | grep -q "Notconfirmed"'
before=$(hidden)
echo toggle-widgets > "$portal.activate"; sleep 1
check 'nothing runs that the user did not confirm' '[ "$(hidden)" = "$before" ]'
check 'no portal errors in the log' '! grep -q "GlobalShortcuts portal failed" "$UMBRA_DATA_DIR/umbra.log"'
$UMBRACTL quit >/dev/null 2>&1
for _ in $(seq 1 20); do kill -0 "$UMBRA_PID" 2>/dev/null || break; sleep 0.5; done
kill "$fake" "$bare_pid" 2>/dev/null
exit $failed
