#!/usr/bin/env bash
# GNOME Wayland pinning through the Umbra Shell extension (#187), in a throwaway headless GNOME Shell:
#   gnome.sh            the extension enabled: Umbra picks native Wayland + the extension; widgets are desktop-layer,
#                       sticky, out of Alt+Tab/overview/window list, at their position, below a normal window and above a
#                       stand-in for the desktop icons; a page asking for the focus gets nothing, a click does; screenshot
#   gnome.sh fallback   the extension not installed: Umbra falls back to XWayland (pinBackend x11)
#
#   dotnet build Umbra.Linux.slnf && linux/test/gnome.sh [fallback]
#
# Everything lives in a temp HOME / XDG_* / XDG_RUNTIME_DIR with its own D-Bus session (dbus-run-session), so the shell's
# settings (dconf), extensions and sockets never touch the real desktop. A second, test-only extension
# (gnome-eval-extension/) turns on unsafe mode in that shell so the test can inspect windows with org.gnome.Shell.Eval.
# Screenshots are taken only inside this headless shell. UMBRA_KEEP=1 keeps the temp folder. Needs gnome-shell 46+, gjs.
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
config=${UMBRA_CONFIGURATION:-Debug}
bin="$repo/src/Umbra.Linux/bin/$config/net10.0/umbra"
ctl="$repo/src/Umbra.Cli/bin/$config/net10.0/umbractl.dll"
uuid=umbra@aminechr54.github.io
mode=${1:-extension}

if [ "${UMBRA_GNOME_INSIDE:-}" = "" ]; then
  for tool in gnome-shell dbus-run-session gdbus gjs gsettings; do command -v $tool >/dev/null || { echo "gnome.sh: $tool is not installed" >&2; exit 2; }; done
  [ -x "$bin" ] || { echo "gnome.sh: $bin not found; run: dotnet build Umbra.Linux.slnf" >&2; exit 2; }
  tmp=$(mktemp -d "${TMPDIR:-/tmp}/umbra-gnome-XXXXXX")
  chmod 700 "$tmp"
  # The session's gvfs and document-portal FUSE mounts can outlive it by a moment: unmount them before removing the folder.
  cleanup() { if [ "${UMBRA_KEEP:-}" = 1 ]; then echo "kept: $tmp" >&2; else fusermount3 -uz "$tmp/run/gvfs" 2>/dev/null; fusermount3 -uz "$tmp/run/doc" 2>/dev/null; rm -rf "$tmp" 2>/dev/null || { sleep 2; rm -rf "$tmp"; }; fi; }
  trap cleanup EXIT
  mkdir -p "$tmp/home" "$tmp/config" "$tmp/data/gnome-shell/extensions" "$tmp/cache" "$tmp/state" "$tmp/run" "$tmp/profile/widgets" "$tmp/bin"
  chmod 700 "$tmp/run"
  # The zip extensions.gnome.org takes (and `gnome-extensions install` reads) must build from the folder as it is.
  if gnome-extensions pack "$repo/linux/gnome-extension/$uuid" --out-dir="$tmp" --force >/dev/null 2>&1 \
      && unzip -l "$tmp/$uuid.shell-extension.zip" 2>/dev/null | grep -q metadata.json; then echo "ok    gnome-extensions pack builds $uuid.shell-extension.zip"
  else echo "FAIL  gnome-extensions pack"; exit 1; fi
  cp -r "$here/gnome-eval-extension/umbra-test-eval@umbra.test" "$tmp/data/gnome-shell/extensions/"
  [ "$mode" = fallback ] || cp -r "$repo/linux/gnome-extension/$uuid" "$tmp/data/gnome-shell/extensions/"
  cp -r "$here/edit-widget" "$tmp/profile/widgets/edit-widget"
  cp -r "$here/focus-widget" "$tmp/profile/widgets/focus-widget"
  printf '#!/bin/sh\nprintf "%%s\\n" "$1" >> "%s"\n' "$tmp/opened.txt" > "$tmp/bin/xdg-open"; chmod +x "$tmp/bin/xdg-open"
  env -i PATH="$tmp/bin:$PATH" HOME="$tmp/home" XDG_CONFIG_HOME="$tmp/config" XDG_DATA_HOME="$tmp/data" XDG_CACHE_HOME="$tmp/cache" \
    XDG_STATE_HOME="$tmp/state" XDG_RUNTIME_DIR="$tmp/run" XDG_CURRENT_DESKTOP=GNOME XDG_SESSION_TYPE=wayland LANG=C.UTF-8 \
    ${DOTNET_ROOT:+DOTNET_ROOT="$DOTNET_ROOT"} DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    UMBRA_GNOME_HOLD="${UMBRA_GNOME_HOLD:-}" UMBRA_GNOME_INSIDE=1 UMBRA_TMP="$tmp" TMPDIR="$tmp" UMBRA_DATA_DIR="$tmp/profile" UMBRA_BIN="$bin" UMBRACTL="dotnet $ctl" UMBRA_OUT="${UMBRA_OUT:-}" \
    dbus-run-session -- "$0" "$mode"
  code=$?
  if [ $code -ne 0 ]; then
    echo "--- umbra.log (last 40 lines)" >&2; tail -n 40 "$tmp/profile/umbra.log" >&2 2>/dev/null
    echo "--- gnome-shell (last 30 lines)" >&2; tail -n 30 "$tmp/shell.log" >&2 2>/dev/null
  fi
  exit $code
fi

# ----- inside the throwaway D-Bus session -----
tmp=$UMBRA_TMP
failed=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failed=1; fi; }
ev() { gdbus call --session --dest org.gnome.Shell --object-path /org/gnome/Shell --method org.gnome.Shell.Eval "$1" 2>/dev/null; }
# The value of a JS expression as a bare string ("" when Eval failed).
js() { ev "$1" | sed -n "s/^(true, '\(.*\)')$/\1/p" | tr -d '\\' | sed 's/^"\(.*\)"$/\1/'; }

exts="['umbra-test-eval@umbra.test'$([ "$mode" = fallback ] || echo ", '$uuid'")]"
gsettings set org.gnome.shell enabled-extensions "$exts"
gsettings set org.gnome.shell disable-user-extensions false
gsettings set org.gnome.shell welcome-dialog-last-shown-version '9999'
gnome-shell --headless --wayland --virtual-monitor 1920x1080 --wayland-display umbra-gnome-test > "$tmp/shell.log" 2>&1 &
shell=$!
for _ in $(seq 1 60); do [ "$(js '1+1')" = 2 ] && break; sleep 0.5; done
check 'the headless GNOME Shell runs with Eval' '[ "$(js "1+1")" = 2 ]'
echo "      $(gnome-shell --version)"
export WAYLAND_DISPLAY=umbra-gnome-test
display=$(js 'imports.gi.GLib.getenv("DISPLAY") ?? ""')
[ -n "$display" ] && export DISPLAY=$display
xauth=$(js 'imports.gi.GLib.getenv("XAUTHORITY") ?? ""')
[ -n "$xauth" ] && export XAUTHORITY=$xauth

if [ "$mode" != fallback ]; then
  for _ in $(seq 1 20); do gdbus call --session --dest io.github.aminechr54.Umbra.Shell --object-path /io/github/aminechr54/Umbra/Shell \
      --method io.github.aminechr54.Umbra.Shell.GetVersion >/dev/null 2>&1 && break; sleep 0.5; done
  check 'the Umbra extension answers on the session bus' 'gdbus call --session --dest io.github.aminechr54.Umbra.Shell --object-path /io/github/aminechr54/Umbra/Shell --method io.github.aminechr54.Umbra.Shell.GetVersion | grep -q "uint32 1"'
fi

"$UMBRA_BIN" --background > "$tmp/profile/stdout.txt" 2>&1 &
umbra=$!
for _ in $(seq 1 40); do $UMBRACTL status >/dev/null 2>&1 && break; sleep 0.5; done
sleep 3
status=$($UMBRACTL status 2>&1)
# Every umbractl call below must reach this throwaway host, never an installed Umbra.
if ! echo "$status" | grep -q "\"dataDir\": \"$UMBRA_DATA_DIR\""; then
  echo "FAIL  umbractl does not reach the test host (dataDir is not $UMBRA_DATA_DIR): stopping"; kill $umbra $shell 2>/dev/null; exit 1
fi
[ -n "${UMBRA_GNOME_DEBUG:-}" ] && { echo "$status"; js 'JSON.stringify(global.display.list_all_windows().map(w => [w.get_title(), w.get_wm_class()]))'; }

if [ "$mode" = fallback ]; then
  check 'without the extension Umbra falls back to XWayland' 'echo "$status" | grep -q "\"pinBackend\": \"x11\""'
  check 'the log says why' 'grep -q "without layer-shell or the Umbra GNOME extension" "$tmp/profile/umbra.log"'
  # Launching Umbra again opens Settings; that window shows the extension hint, and only that one (recorded).
  "$UMBRA_BIN" >/dev/null 2>&1; sleep 3
  check 'Settings offered the extension (once: recorded)' '[ -f "$UMBRA_DATA_DIR/gnome-extension-hint-shown" ]'
  $UMBRACTL capture "settings:general" "$tmp/fallback-settings.png" >/dev/null 2>&1
  sleep 1.5; $UMBRACTL capture "settings:general" "$tmp/fallback-settings.png" >/dev/null 2>&1 # after the page switch settled
  check 'Settings can be captured' '[ -s "$tmp/fallback-settings.png" ]'
  [ -n "$UMBRA_OUT" ] && cp "$tmp/fallback-settings.png" "$UMBRA_OUT/" 2>/dev/null
else
  check 'Umbra uses native Wayland + the extension' 'echo "$status" | grep -q "\"pinBackend\": \"gnome-extension\""'
  $UMBRACTL configure edit-widget --x 300 --y 240 >/dev/null
  $UMBRACTL configure focus-widget --x 300 --y 600 >/dev/null; sleep 1.5
  q='const w = global.display.list_all_windows().find(w => /^umbra:[0-9a-f]{32}$/.test(w.get_title() ?? "") && w.get_frame_rect().y < 500)'
  info=$(js "$q; w ? JSON.stringify({cls: w.get_wm_class(), wayland: w.get_client_type() === imports.gi.Meta.WindowClientType.WAYLAND, desktop: w.get_window_type() === imports.gi.Meta.WindowType.DESKTOP, sticky: w.is_on_all_workspaces(), skip: w.is_skip_taskbar(), x: w.get_frame_rect().x, y: w.get_frame_rect().y}) : 'none'")
  echo "      widget window: $info"
  check 'the widget is a native Wayland window with the app id' 'echo "$info" | grep -q "\"cls\":\"io.github.aminechr54.Umbra\",\"wayland\":true"'
  check 'it is a desktop-layer window' 'echo "$info" | grep -q "\"desktop\":true"'
  check 'it is on every workspace' 'echo "$info" | grep -q "\"sticky\":true"'
  check 'it is skipped by the window list (dock, taskbar)' 'echo "$info" | grep -q "\"skip\":true"'
  check 'it is at 300,240 as configured' 'echo "$info" | grep -q "\"x\":300,\"y\":240"'
  check 'it is not in Alt+Tab' '[ "$(js "$q; String(global.display.get_tab_list(imports.gi.Meta.TabList.NORMAL_ALL, null).includes(w))")" = false ]'

  # A normal window and a stand-in for the desktop icons (DING also makes its window a desktop window, kept at the bottom).
  cat > "$tmp/other.js" <<'EOF'
imports.gi.versions.Gtk = '4.0';
const {Gtk, GLib} = imports.gi;
const app = new Gtk.Application({application_id: 'org.umbra.Test' + ARGV[0].replace(/[^a-z]/g, ''), flags: imports.gi.Gio.ApplicationFlags.NON_UNIQUE});
app.connect('activate', () => {
  const w = new Gtk.ApplicationWindow({application: app, title: ARGV[0], default_width: 500, default_height: 400});
  const entry = new Gtk.Entry();
  entry.connect('changed', () => GLib.file_set_contents(ARGV[1], entry.get_text()));
  w.set_child(entry); w.present();
});
app.run([]);
EOF
  GDK_BACKEND=wayland gjs "$tmp/other.js" "a normal window" "$tmp/other-typed.txt" >/dev/null 2>&1 & other=$!
  GDK_BACKEND=wayland gjs "$tmp/other.js" "fake desktop icons" "$tmp/icons-typed.txt" >/dev/null 2>&1 & icons=$!
  # Wait until a window with this title is shown (a gjs start can take several seconds on a busy machine).
  shown() { for _ in $(seq 1 40); do [ "$(js "String(global.display.list_all_windows().some(w => w.get_title() === '$1'))")" = true ] && return 0; sleep 0.25; done; return 1; }
  check 'the two test windows are up' 'shown "a normal window" && shown "fake desktop icons"'
  sleep 0.5
  js 'const i = global.display.list_all_windows().find(w => w.get_title() === "fake desktop icons"); i.set_type(imports.gi.Meta.WindowType.DESKTOP); i.stick(); i.move_frame(false, 0, 330); /* 500x400: overlaps the bottom of the widget at 300,240, not the spots clicked below */ "ok"' >/dev/null
  js 'const o = global.display.list_all_windows().find(w => w.get_title() === "a normal window"); o.move_frame(false, 200, 150); "ok"' >/dev/null
  sleep 0.5
  # DING raises and lowers its own desktop windows; the widget must end up above it whatever it does.
  js 'const i = global.display.list_all_windows().find(w => w.get_title() === "fake desktop icons"); i.raise(); "ok"' >/dev/null
  sleep 1
  stack() { js 'JSON.stringify(global.get_window_actors().map(a => a.meta_window).filter(w => w.get_title() !== "Umbra").map(w => /^umbra:/.test(w.get_title() ?? "") ? "umbra" : w.get_title()))'; }
  for _ in 1 2 3 4 5 6; do order=$(stack); echo "$order" | grep -q "fake desktop icons.*umbra.*a normal window" && break; sleep 0.5; done # the painted order follows a restack a frame later
  echo "      stacking, bottom to top: $order"
  check 'the widget is above the desktop icons (even after they raised themselves) and below the normal window' 'echo "$order" | grep -q "fake desktop icons.*umbra.*a normal window"'
  check 'the overview leaves it out (desktop windows and skip-taskbar windows are not overview windows)' \
    '[ "$(js "$q; String(w.get_window_type() === imports.gi.Meta.WindowType.DESKTOP && w.is_skip_taskbar())")" = true ]'

  # Focus gating, with the compositor's own virtual pointer and keyboard as the "real" input.
  kb='globalThis.__kb ??= global.stage.context.get_backend().get_default_seat().create_virtual_device(imports.gi.Clutter.InputDeviceType.KEYBOARD_DEVICE)'
  ptr='globalThis.__ptr ??= global.stage.context.get_backend().get_default_seat().create_virtual_device(imports.gi.Clutter.InputDeviceType.POINTER_DEVICE)'
  type() { for c in $(echo "$1" | fold -w1); do
      js "$kb; const t = imports.gi.GLib.get_monotonic_time(); const k = imports.gi.Clutter.unicode_to_keysym('$c'.charCodeAt(0)); __kb.notify_keyval(t, k, imports.gi.Clutter.KeyState.PRESSED); __kb.notify_keyval(t + 1000, k, imports.gi.Clutter.KeyState.RELEASED); 'ok'" >/dev/null; sleep 0.05; done; }
  click() { js "$ptr; const C = imports.gi.Clutter; const t = imports.gi.GLib.get_monotonic_time(); __ptr.notify_absolute_motion(t, $1, $2); __ptr.notify_button(t + 1000, C.BUTTON_PRIMARY, C.ButtonState.PRESSED); 'ok'" >/dev/null
      sleep 0.25; js "$ptr; const C = imports.gi.Clutter; __ptr.notify_button(imports.gi.GLib.get_monotonic_time(), C.BUTTON_PRIMARY, C.ButtonState.RELEASED); 'ok'" >/dev/null; }
  focus() { js 'const f = global.display.focus_window; f ? (/^umbra:/.test(f.get_title() ?? "") ? "umbra" : f.get_title()) : "none"'; }
  focus_is() { for _ in $(seq 1 12); do [ "$(focus)" = "$1" ] && return 0; sleep 0.25; done; return 1; } # within ~3 s
  # Clicks a window wherever it really is (a move asked of a just-mapped window may not land), in its lower part: the
  # shell's "unsafe mode" banner (from the test-only Eval extension) sits at the top centre and would take the click.
  clickon() { local at; at=$(js "const w = global.display.list_all_windows().find(w => w.get_title() === '$1'); const r = w.get_frame_rect(); (r.x + (r.width >> 1)) + ' ' + (r.y + ((r.height * 7) >> 3))"); click $at; }
  data="$UMBRA_DATA_DIR/data/edit-widget.json"
  js "$kb; $ptr; 'ok'" >/dev/null; sleep 0.5 # the virtual devices exist before their first event
  check 'the page asked for the focus three times by itself (edit-widget) and got nothing' 'grep -q "\"attacked\":true" "$data" && [ "$(focus)" != umbra ]'
  clickon "a normal window"
  # A dropped first press is reported, and allowed once; a second miss fails the check below.
  focus_is "a normal window" || { echo "note  the first click on the normal window was lost (focus: $(focus)); clicking again"; clickon "a normal window"; }
  check "a click on the normal window focuses it (has: $(focus))" 'focus_is "a normal window"'
  if [ -n "${UMBRA_GNOME_HOLD:-}" ]; then echo "$DBUS_SESSION_BUS_ADDRESS" > "$tmp/bus"; echo "holding: $tmp/bus"; sleep "$UMBRA_GNOME_HOLD"; fi
  type "abc"; sleep 0.5
  check 'typing goes to the normal window' 'grep -q abc "$tmp/other-typed.txt"'
  check 'nothing reached the widget' '! grep -q "\"typed\":\"[a-z]" "$data"'
  # Move the normal window off the widget, then click the widget where it has no text field: the page does not ask
  # for the focus there, so the compositor's focus-on-click is handed back.
  js 'const o = global.display.list_all_windows().find(w => w.get_title() === "a normal window"); o.move_frame(false, 1300, 500); "ok"' >/dev/null
  sleep 0.5
  click 500 250; sleep 1
  check "a click on the widget's label leaves the focus where it was (has: $(focus))" '[ "$(focus)" = "a normal window" ]'
  # A click into the widget's text field: the page asks right after the press, the host and the extension grant it.
  click 360 300; sleep 1
  check "after a real click into the widget's text field it has the focus (has: $(focus))" 'focus_is umbra'
  type "hello"; sleep 1
  check "typing after the click reaches the widget ($(grep -o '"typed":"[^"]*"' "$data"))" 'grep -q "\"typed\":\"hello\"" "$data"'
  clickon "a normal window"; sleep 1
  check "a click on the normal window takes the focus back (has: $(focus))" 'focus_is "a normal window"'
  type "xyz"; sleep 0.5
  check 'and typing goes there again, not to the widget' 'grep -q xyz "$tmp/other-typed.txt" && grep -q "\"typed\":\"hello\"" "$data"'
  # The hostile page (focus-widget at 300,600): focus(), autofocus, element.focus() and focus messages every 100 ms.
  fdata="$UMBRA_DATA_DIR/data/focus-widget.json"
  fkeys() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get("keys","") + "|" + d.get("typed",""))' "$fdata" 2>/dev/null; }
  never_umbra() { for _ in $(seq 1 $(( $1 * 10 ))); do [ "$(focus)" = umbra ] && return 1; sleep 0.1; done; return 0; }
  check "the hostile page is asking ($(grep -o '"asks":[0-9]*' "$fdata"))" 'grep -q "\"asks\":[1-9]" "$fdata"'
  for c in t h e q u i c k f o x; do type $c; sleep 0.15; done; sleep 0.5
  check "typing for 2.5 s while it asks goes to the normal window ($(cat "$tmp/other-typed.txt"))" 'grep -q "xyzthequickfox" "$tmp/other-typed.txt"'
  check "no keystroke reached the hostile widget (keys|typed: $(fkeys))" '[ "$(fkeys)" = "|" ]'
  check 'and the focus never moved to a widget over 2 s more of asking' 'never_umbra 2'
  click 400 675; sleep 1
  check "a click into its field lends it the keyboard (has: $(focus))" 'focus_is umbra'
  type "hi"; sleep 0.8
  check "typing after the click reaches it (keys|typed: $(fkeys))" '[ "$(fkeys)" = "hi|hi" ]'
  clickon "a normal window"; sleep 0.5
  check "a click back on the normal window takes the keyboard back (has: $(focus))" 'focus_is "a normal window"'
  # Clicking the normal window 200 ms after the widget: the page asking meanwhile must not get the keyboard back.
  click 400 675; sleep 0.2; clickon "a normal window"; sleep 0.3
  check "clicked the widget and 200 ms later the normal window: it keeps the focus over 2 s of asking (has: $(focus))" 'focus_is "a normal window" && never_umbra 2'
  type "q1"; sleep 0.5
  check "typing goes to the normal window, not the widget (keys|typed: $(fkeys))" 'grep -q "q1" "$tmp/other-typed.txt" && [ "$(fkeys)" = "hi|hi" ]'
  # The focus falling back when the active window closes, with the pointer resting over the widget (no press). Every
  # other normal window is closed first, so the fallback has only the widget (or the stand-in desktop window) to go to:
  # the widget must not keep it, and the extension must not count that focus as a press.
  js 'const o = global.display.list_all_windows().find(w => w.get_title() === "a normal window"); o.delete(global.display.get_current_time_roundtrip()); "ok"' >/dev/null
  sleep 1
  closing() {
    GDK_BACKEND=wayland gjs "$tmp/other.js" "closing window" "$tmp/closing-typed.txt" >/dev/null 2>&1 & closing=$!
    shown "closing window"; sleep 0.5
    js 'const c = global.display.list_all_windows().find(w => w.get_title() === "closing window"); c.move_frame(false, 900, 100); "ok"' >/dev/null
    sleep 0.5; clickon "closing window"
    check "the closing window has the focus (has: $(focus))" 'focus_is "closing window"'
  }
  close_it() { js 'const c = global.display.list_all_windows().find(w => w.get_title() === "closing window"); c.delete(global.display.get_current_time_roundtrip()); "ok"' >/dev/null; }
  closing
  js "$ptr; __ptr.notify_absolute_motion(imports.gi.GLib.get_monotonic_time(), 500, 250); 'ok'" >/dev/null; sleep 0.3
  close_it; sleep 1.5
  check "only the widget was left: after the window closed with the pointer on it, it does not have the focus (has: $(focus))" '[ "$(focus)" != umbra ]'
  type "qq"; sleep 0.5
  check 'and typing does not reach the widget' 'grep -q "\"typed\":\"hello\"" "$data"'
  kill $closing 2>/dev/null
  # The same with a button held down on the widget's label while the window closes (a press, but not one the page
  # asks the focus for): still no focus for the widget.
  closing
  js "$ptr; const C = imports.gi.Clutter; const t = imports.gi.GLib.get_monotonic_time(); __ptr.notify_absolute_motion(t, 500, 250); __ptr.notify_button(t + 1000, C.BUTTON_PRIMARY, C.ButtonState.PRESSED); 'ok'" >/dev/null
  sleep 0.3; close_it; sleep 1.5
  check "with a button held on the widget while the window closed, the widget does not have the focus (has: $(focus))" '[ "$(focus)" != umbra ]'
  js "$ptr; const C = imports.gi.Clutter; __ptr.notify_button(imports.gi.GLib.get_monotonic_time(), C.BUTTON_PRIMARY, C.ButtonState.RELEASED); 'ok'" >/dev/null
  type "qq"; sleep 0.5
  check 'and typing does not reach the widget' 'grep -q "\"typed\":\"hello\"" "$data"'
  kill $closing 2>/dev/null

  # The compositor's own view (inside this headless shell only) and the widget's own capture.
  js 'const S = imports.gi.Shell; new S.Screenshot().screenshot(false, imports.gi.Gio.File.new_for_path("'"$tmp"'/gnome-screen.png").create(0, null), () => {}); "ok"' >/dev/null
  sleep 1.5
  $UMBRACTL capture edit-widget "$tmp/edit-widget.png" >/dev/null 2>&1
  check 'umbractl capture works on the extension path' '[ -s "$tmp/edit-widget.png" ]'
  [ -n "$UMBRA_OUT" ] && cp "$tmp/gnome-screen.png" "$tmp/edit-widget.png" "$UMBRA_OUT/" 2>/dev/null

  # The extension going away. Disabled and enabled again quickly (a reload): the widget is hidden meanwhile (never a
  # plain window above the user's apps), then registered and pinned again.
  ext() { gdbus call --session --dest org.gnome.Shell.Extensions --object-path /org/gnome/Shell/Extensions --method "org.gnome.Shell.Extensions.$1" "$uuid" >/dev/null; }
  mapped() { js 'String(global.display.list_all_windows().some(w => /^umbra:/.test(w.get_title() ?? "") && w.get_window_type() !== imports.gi.Meta.WindowType.DESKTOP && w.showing_on_its_workspace()))'; }
  pinned() { js 'String(global.display.list_all_windows().filter(w => /^umbra:/.test(w.get_title() ?? "") && w.get_window_type() === imports.gi.Meta.WindowType.DESKTOP && w.showing_on_its_workspace()).length)'; }
  ext DisableExtension; sleep 0.7
  check 'disabled: no Umbra window is left as a plain window' '[ "$(mapped)" = false ]'
  # A widget opened while the extension is away is held back, never shown as a plain window.
  $UMBRACTL add-instance edit-widget >/dev/null 2>&1; sleep 1.5
  check 'a widget opened while it is away is not shown as a plain window' '[ "$(mapped)" = false ]'
  ext EnableExtension; sleep 3
  check "enabled again within 3 s: all three widgets registered and pinned (pinned: $(pinned))" '[ "$(pinned)" = 3 ]'
  check 'the first one is back at 300,240' '[ "$(js "String(global.display.list_all_windows().some(w => /^umbra:/.test(w.get_title() ?? \"\") && w.get_frame_rect().x === 300 && w.get_frame_rect().y === 240))")" = true ]'
  check "and neither took the focus after the reload (has: $(focus))" '[ "$(focus)" != umbra ]'
  # Disabled, and someone else takes the free bus name: it is not trusted (no protocol answer), the widgets stay
  # hidden, and Umbra restarts on XWayland.
  ext DisableExtension; sleep 0.3
  cat > "$tmp/squatter.js" <<'EOF'
const {Gio, GLib} = imports.gi;
Gio.bus_own_name(Gio.BusType.SESSION, 'io.github.aminechr54.Umbra.Shell', Gio.BusNameOwnerFlags.NONE, null, null, null);
new GLib.MainLoop(null, false).run();
EOF
  gjs "$tmp/squatter.js" >/dev/null 2>&1 & squatter=$!
  sleep 0.7
  check 'a squatter on the bus name: still no plain Umbra window' '[ "$(mapped)" = false ]'
  for _ in $(seq 1 40); do grep -q "not as this Umbra's extension" "$UMBRA_DATA_DIR/umbra.log" && break; sleep 0.25; done
  for _ in $(seq 1 40); do timeout 5 $UMBRACTL status 2>/dev/null | grep -q '"pinBackend": "x11"' && break; sleep 0.5; done
  status=$($UMBRACTL status 2>&1)
  check 'the squatter is not trusted: Umbra restarted on XWayland (pinBackend x11, same data folder)' \
    'echo "$status" | grep -q "\"pinBackend\": \"x11\"" && echo "$status" | grep -q "\"dataDir\": \"$UMBRA_DATA_DIR\""'
  check 'the log says why' 'grep -q "came back, but not as this Umbra.s extension: restarting on XWayland" "$UMBRA_DATA_DIR/umbra.log"'
  kill $squatter 2>/dev/null
  kill $other $icons 2>/dev/null
fi

$UMBRACTL quit >/dev/null 2>&1
for _ in $(seq 1 20); do kill -0 $umbra 2>/dev/null || break; sleep 0.5; done
kill $umbra 2>/dev/null
kill $shell 2>/dev/null; wait $shell 2>/dev/null
[ $failed = 0 ] && echo "GNOME TEST PASSED ($mode)" || echo "GNOME TEST FAILED ($mode)"
exit $failed
