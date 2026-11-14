#!/usr/bin/env bash
# Keyboard focus gating on X11 (#188), headless with xdotool as the "real" mouse and keyboard and a stand-in text editor:
#  - a page that asks for the keyboard every way it can (focus(), window.focus(), autofocus, element.focus() and focus
#    messages every 100 ms, forged pointerdowns) never gets a keystroke typed into the editor;
#  - a click into its text field lends it the keyboard and typing lands there; a click back into the editor takes it
#    back, even right after the click on the widget while the page keeps asking (one press, one grant);
#  - the window takes no WM_TAKE_FOCUS (edit.sh checks that a click outside a field of a quiet page leaves the focus);
#  - a press is redeemable only while current (button down, or just released): a page asking 600 ms after a click on
#    its label, when the user already clicked the editor, gets nothing;
#  - a widget focused from outside without a grant loses the keyboard at once;
#  - a widget switched off and on again does not take the keyboard by appearing.
# The same steps for the GNOME extension are in gnome.sh; layer-shell (KDE Plasma) is a human check (no headless compositor
# with layer-shell here).
#
#   dotnet build Umbra.Linux.slnf && linux/test/focus.sh
set -u
here=$(cd "$(dirname "$0")" && pwd)
if [ "${UMBRA_PID:-}" = "" ]; then
  for tool in xdotool gjs; do command -v $tool >/dev/null || { echo "focus.sh: $tool is not installed" >&2; exit 2; }; done
  json=$(mktemp); printf '{"general":{"logLevel":"verbose","lockAll":true}}' > "$json"
  "$here/headless.sh" -w "$here/focus-widget" -j "$json" -s 3 -- "$0"; code=$?
  rm -f "$json"; exit $code
fi

# ----- inside the session -----
failed=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failed=1; fi; }
data="$UMBRA_DATA_DIR/data/focus-widget.json"
typed="$UMBRA_DATA_DIR/editor.txt"; : > "$typed"
focused() { xdotool getwindowfocus 2>/dev/null; }
keys() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get("keys","") + "|" + d.get("typed",""))' "$data" 2>/dev/null; }
asks() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("asks",0))' "$data" 2>/dev/null || echo 0; }
# Samples the X input focus every 50 ms for $1 seconds; succeeds when it never was on window $2 (fails without a window).
never_on() { [ -n "$2" ] || return 1; local n=$(( $1 * 20 )); for _ in $(seq 1 $n); do [ "$(focused)" = "$2" ] && return 1; sleep 0.05; done; return 0; }
click_at() { xdotool mousemove "$1" "$2"; sleep 0.15; xdotool click 1; }
editor_click() { eval "$(xdotool getwindowgeometry --shell "$ed")"; click_at $((X + WIDTH / 2)) $((Y + 20)); }
label_click() { eval "$(xdotool getwindowgeometry --shell "$w")"; click_at $((X + 20)) $((Y + 8)); }
late() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("lateAsked",0))' "$data" 2>/dev/null || echo 0; }
field_click() { eval "$(xdotool getwindowgeometry --shell "$w")"; click_at $((X + WIDTH / 2)) $((Y + HEIGHT * 55 / 100)); }

$UMBRACTL status | grep -q "\"dataDir\": \"$UMBRA_DATA_DIR\"" || { echo "FAIL  umbractl does not reach the test host"; exit 1; }
for _ in $(seq 1 40); do [ "$(asks)" -gt 0 ] 2>/dev/null && break; sleep 0.5; done
w=$(xdotool search --name "Umbra - Focus widget" | head -1)
check 'the hostile page runs and keeps asking for the keyboard' '[ -n "$w" ] && [ "$(asks)" -gt 0 ]'
check 'it did not get the keyboard by appearing (autofocus, focus() at load)' '[ "$(focused)" != "$w" ]'
check 'its window takes no WM_TAKE_FOCUS and accepts no input on its own' \
  '! xprop -id "$w" WM_PROTOCOLS | grep -q WM_TAKE_FOCUS && xprop -id "$w" WM_HINTS | grep -q "input focus: False"'

GDK_BACKEND=x11 gjs "$here/fake-editor.js" "fake editor" "$typed" >/dev/null 2>&1 & editor=$!
for _ in $(seq 1 40); do ed=$(xdotool search --name "^fake editor$" | head -1); [ -n "$ed" ] && break; sleep 0.25; done
[ -n "$ed" ] && [ -n "$w" ] || { echo "FAIL  the editor or the widget window did not appear"; kill $editor 2>/dev/null; exit 1; }
xdotool windowmove "$ed" 1000 100; sleep 0.5
editor_click; sleep 0.5
check "a click on the editor focuses it" '[ "$(focused)" = "$ed" ]'

before=$(asks)
xdotool type --delay 150 "thequickbrownfox"; sleep 0.5
check "typing for 2.5 s while the page asks for the keyboard goes to the editor ($(cat "$typed"))" '[ "$(cat "$typed")" = thequickbrownfox ]'
check "no keystroke reached the widget (keys|typed: $(keys))" '[ "$(keys)" = "|" ]'
check "the page kept asking meanwhile (asks $before -> $(asks))" '[ "$(asks)" -gt "$before" ]'
check 'and the X focus never moved to the widget over 3 s more of asking' 'never_on 3 "$w"'

field_click; sleep 1
check "a click into the widget's text field lends it the keyboard (focus: $(focused))" '[ "$(focused)" = "$w" ]'
xdotool type --delay 40 "hello"; sleep 0.8
check "typing after the click reaches the widget (keys|typed: $(keys))" '[ "$(keys)" = "hello|hello" ]'

editor_click; sleep 0.5
check "a click back into the editor takes the keyboard back (focus: $(focused))" '[ "$(focused)" = "$ed" ]'
xdotool type --delay 40 "xyz"; sleep 0.5
check "typing goes to the editor again ($(cat "$typed")), not the widget ($(keys))" '[ "$(cat "$typed")" = thequickbrownfoxxyz ] && [ "$(keys)" = "hello|hello" ]'

# The page asks every 100 ms: clicking the editor 200 ms after the widget must not let the next ask take it back.
field_click; sleep 0.2; editor_click
check 'clicked the widget and 200 ms later the editor: the page asking meanwhile never gets the keyboard back' 'sleep 0.3; never_on 2 "$w"'
xdotool type --delay 40 "q1"; sleep 0.5
check "typing goes to the editor ($(cat "$typed")), not the widget ($(keys))" '[ "$(cat "$typed")" = thequickbrownfoxxyzq1 ] && [ "$(keys)" = "hello|hello" ]'

# A press the page redeems late: a click on its label (the page stops asking and asks once, 600 ms after its own
# pointerdown), then the editor at ~300 ms. The press is no longer current when the ask comes: the editor keeps typing.
lb=$(late)
label_click; sleep 0.15; editor_click; sleep 1
check "the page asked late, after the user had clicked the editor (late asks $lb -> $(late))" '[ "$(late)" -gt "$lb" ]'
check "and did not get the keyboard (focus: $(focused))" '[ "$(focused)" = "$ed" ] && never_on 1 "$w"'
xdotool type --delay 40 "r2"; sleep 0.5
check "typing goes to the editor ($(cat "$typed")), not the widget ($(keys))" '[ "$(cat "$typed")" = thequickbrownfoxxyzq1r2 ] && [ "$(keys)" = "hello|hello" ]'

# Something focuses the widget without a grant (a window manager ignoring the input hint; here xdotool's XSetInputFocus):
# the host takes the keyboard off it at once.
xdotool windowfocus "$w" 2>/dev/null; sleep 1
check "focused from outside without a grant, the widget loses the keyboard again (focus: $(focused))" '[ "$(focused)" != "$w" ]'
check 'the host logged it' 'grep -q "became active without a focus grant" "$UMBRA_DATA_DIR/umbra.log"'
xdotool type --delay 40 "s3"; sleep 0.5
check "no keystroke reached the widget ($(keys)); the window manager gave the editor its keyboard back ($(cat "$typed"))" '[ "$(keys)" = "hello|hello" ] && [ "$(cat "$typed")" = thequickbrownfoxxyzq1r2s3 ]'
editor_click; sleep 0.5

# Switched off (its window closes) and on again: the new window must not take the keyboard by appearing.
$UMBRACTL configure focus-widget --enabled false >/dev/null; sleep 1
$UMBRACTL configure focus-widget --enabled true >/dev/null
for _ in $(seq 1 40); do w2=$(xdotool search --name "Umbra - Focus widget" | head -1); [ -n "$w2" ] && break; sleep 0.25; done
check 'a widget switched on again does not take the keyboard while it loads and asks' '[ -n "$w2" ] && never_on 3 "$w2" && [ "$(focused)" = "$ed" ]'
xdotool type --delay 40 "z9"; sleep 0.5
check "typing goes to the editor ($(cat "$typed"))" '[ "$(cat "$typed")" = thequickbrownfoxxyzq1r2s3z9 ]'
check 'the host logged the refused requests' 'grep -q "focus ignored: no current, unused mouse press" "$UMBRA_DATA_DIR/umbra.log"'

kill $editor 2>/dev/null
[ $failed = 0 ] && echo "FOCUS TEST PASSED (x11)" || echo "FOCUS TEST FAILED (x11)"
exit $failed
