#!/usr/bin/env bash
# Opening a .umbrawidget from the file manager (#365) in a throwaway session: a second `umbra <file>` (what the .desktop
# entry's %f runs) hands the file to the running instance, which shows the host-drawn review. Cancel (and Esc) installs nothing
# and leaves no staging folder; Install installs; a package declaring hosts lists them in the review; a symlink, a FIFO or
# a non-package path never reaches a review; a first instance started with a file reviews it too.
#   linux/test/open-package.sh [shot-dir]     Needs what headless.sh needs, plus xdotool and ImageMagick (import).
# With shot-dir, screenshots of this session's own Xvfb screen (never the real desktop) are saved there.
set -u
here=$(cd "$(dirname "$0")" && pwd)
export SHOTS=${1:-}
[ -n "$SHOTS" ] && mkdir -p "$SHOTS"
work=$(mktemp -d "${UMBRA_TEST_TMP:-${TMPDIR:-/tmp}}/umbra-pkg-XXXXXX")
trap 'rm -rf "$work"' EXIT

# Two widget folders to pack: one asking for nothing, one declaring a host.
mk() { # id name hosts-json
  mkdir -p "$work/src/$1"
  printf '{"id":"%s","name":"%s","version":"1.0.0","author":"Test","entry":"index.html","width":200,"height":100%s}\n' "$1" "$2" "$3" > "$work/src/$1/widget.json"
  echo '<!doctype html><p style="color:#fff">hi</p>' > "$work/src/$1/index.html"
}
mk plain-pkg "Plain package" ''
mk hosts-pkg "Hosts package" ',"permissions":{"hosts":["api.example.com"]}'
export WORK="$work"

"$here/headless.sh" -s 4 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  ok() { echo "ok    $*"; }
  log() { cat "$UMBRA_DATA_DIR/umbra.log"; }
  installed() { [ -f "$UMBRA_DATA_DIR/widgets/$1/widget.json" ]; }
  staging() { find "$TMPDIR" -maxdepth 1 \( -name "umbra-open-*" -o -name "umbra-local-*" \) | wc -l; }
  shot() { [ -n "$SHOTS" ] && import -window root "$SHOTS/$1.png"; }
  # The review or a refusal notice: a GTK alert dialog (title-less and wider than a widget; a one-line notice is ~60 px tall).
  review() { for _ in $(seq 1 30); do w=$(xdotool search --onlyvisible --classname umbra 2>/dev/null | while read -r id; do n=$(xdotool getwindowname "$id" 2>/dev/null); [ -z "$n" ] && eval "$(xdotool getwindowgeometry --shell "$id")" && [ "$WIDTH" -gt 200 ] && [ "$HEIGHT" -gt 40 ] && echo "$id"; done | tail -1); [ -n "$w" ] && { echo "$w"; return; }; sleep 0.5; done; }
  # Clicks the right-hand button of the dialog (Install / Replace / Update; Cancel is on the left and is the default).
  click_at() { eval "$(xdotool getwindowgeometry --shell "$1")"; xdotool mousemove $((X + WIDTH * $2 / 4)) $((Y + HEIGHT - 25)); sleep 0.3; xdotool click 1; sleep 2; }
  click_yes() { click_at "$1" 3; }
  click_no() { click_at "$1" 1; }
  # The refusal notice (one OK button, the default): Return on the focused dialog, more reliable than aiming at a lone button.
  click_ok() { xdotool windowactivate --sync "$1" 2>/dev/null; xdotool windowfocus --sync "$1" 2>/dev/null; xdotool key Return; sleep 1.5; }

  for _ in $(seq 1 60); do $UMBRACTL status >/dev/null 2>&1 && break; sleep 0.5; done
  $UMBRACTL status | grep -q "\"dataDir\": \"$UMBRA_DATA_DIR\"" || $UMBRACTL status | grep -q "$UMBRA_DATA_DIR" || fail "umbractl is not talking to the throwaway profile"
  out=$($UMBRACTL pack "$WORK/src/plain-pkg" -o "$WORK/plain.umbrawidget" 2>&1) || fail "pack plain: $out"
  $UMBRACTL pack "$WORK/src/hosts-pkg" -o "$WORK/hosts.umbrawidget" >/dev/null || fail "pack hosts"
  ok "packed two packages with umbractl pack"

  # 1. A second umbra with the file, from another folder and with a relative name: the review opens; Esc cancels.
  (cd "$WORK" && timeout 15 "$UMBRA_BIN" plain.umbrawidget) || fail "the second umbra did not exit cleanly"
  kill -0 "$UMBRA_PID" || fail "the first umbra died"
  w=$(review); [ -n "$w" ] || { log | tail -5; fail "no review dialog after a second launch with the file"; }
  log | grep -q "Reviewing plain-pkg 1.0.0 from $WORK/plain.umbrawidget" || fail "the review is not for the full path"
  shot review-plain
  ok "a second umbra <file> opened the host-drawn review in the running instance"
  click_no "$w"
  xdotool search --onlyvisible --classname umbra 2>/dev/null | grep -qx "$w" && fail "Cancel did not close the review"
  installed plain-pkg && fail "Cancel installed the widget"
  [ "$(staging)" = 0 ] || fail "staging left behind after Cancel: $(find "$TMPDIR" -maxdepth 1 -name "umbra-*")"
  log | grep -q "Install of plain-pkg from plain.umbrawidget cancelled" || fail "no cancel in the log"
  ok "Cancel installs nothing and leaves no staging folder"

  # 2. Again, then Install.
  timeout 15 "$UMBRA_BIN" "$WORK/plain.umbrawidget" || fail "second launch 2"
  w=$(review); [ -n "$w" ] || fail "no review the second time"
  click_yes "$w"
  installed plain-pkg || { log | tail -5; fail "Install did not install the widget"; }
  $UMBRACTL list | grep -q "\"plain-pkg\"" || fail "umbractl list does not show the installed widget"
  [ "$(staging)" = 0 ] || fail "staging left behind after Install"
  ok "Install installs it (listed by umbractl, no staging left)"

  # 3. A package declaring a host: the review lists it; a second request while it is open is ignored.
  timeout 15 "$UMBRA_BIN" "$WORK/hosts.umbrawidget" || fail "second launch 3"
  w=$(review); [ -n "$w" ] || fail "no review for the hosts package"
  shot review-hosts
  timeout 15 "$UMBRA_BIN" "$WORK/plain.umbrawidget"; sleep 1.5
  log | grep -q "Install review already open" || fail "a second request while the review is open was not ignored"
  ok "one review at a time"
  log | grep -q "Reviewing hosts-pkg" || fail "hosts review not logged"
  xdotool windowactivate --sync "$w" 2>/dev/null; xdotool key Escape; sleep 1.5
  xdotool search --onlyvisible --classname umbra 2>/dev/null | grep -qx "$w" && fail "Esc did not close the review"
  installed hosts-pkg && fail "Cancel installed the hosts package"
  timeout 15 "$UMBRA_BIN" "$WORK/hosts.umbrawidget"; w=$(review); [ -n "$w" ] || fail "no second hosts review"
  click_yes "$w"
  installed hosts-pkg || fail "Install did not install the hosts package"
  ok "the hosts package is reviewed (see review-hosts.png) and installs on Install"

  # 4. What never reaches a review: a symlink, a FIFO, a path that is not a package, a raw pipe line with a relative path.
  ln -s "$WORK/plain.umbrawidget" "$WORK/link.umbrawidget"
  timeout 15 "$UMBRA_BIN" "$WORK/link.umbrawidget"; sleep 2
  log | grep -q "symbolic link" || fail "a symlink was not refused"
  w=$(review); [ -n "$w" ] && click_ok "$w" # the refusal notice (OK)
  mkfifo "$WORK/fifo.umbrawidget"
  timeout 15 "$UMBRA_BIN" "$WORK/fifo.umbrawidget"; sleep 2
  log | grep -q "Not a regular file" || fail "a FIFO was not refused"
  w=$(review); [ -n "$w" ] && click_ok "$w"
  printf "open-package\trelative.umbrawidget\n" | python3 -I -c "
import socket,sys,glob,os
s=socket.socket(socket.AF_UNIX); s.connect(glob.glob(os.path.join(os.environ[\"TMPDIR\"], \"CoreFxPipe_*\"))[0]); s.sendall(sys.stdin.buffer.read()); s.close()"
  sleep 1
  log | grep -q "Ignored open-package request (Not an absolute path)" || fail "a relative path on the pipe was not refused"
  kill -0 "$UMBRA_PID" || fail "umbra died"
  ok "a symlink, a FIFO and a relative pipe path are refused without a review"

  # 5. A first instance started with a file reviews it (and opens no Settings window).
  $UMBRACTL remove plain-pkg >/dev/null 2>&1
  $UMBRACTL quit >/dev/null 2>&1; sleep 2
  "$UMBRA_BIN" "$WORK/plain.umbrawidget" >/dev/null 2>&1 &
  first=$!
  w=$(review); [ -n "$w" ] || fail "a first instance with a file showed no review"
  click_yes "$w"
  installed plain-pkg || fail "the first instance did not install on Install"
  ok "a first umbra <file> shows the review and installs on Install"
  kill $first 2>/dev/null
  echo "OPEN-PACKAGE TEST PASSED"
'
