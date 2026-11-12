#!/usr/bin/env bash
# Crash recovery (#185), headless: kills the clock widget's web process. The first three crashes must reload
# the widget; the fourth inside ten minutes must show the host's "This widget stopped working" page; a real click on its
# Reload button must bring the widget back.
#
#   dotnet build Umbra.Linux.slnf && linux/test/crash.sh [out-dir]
#
# The signal is SIGKILL by default: with SIGSEGV a desktop system first writes a core dump (apport, systemd-coredump),
# which can take most of a minute per crash. UMBRA_CRASH_SIGNAL=SEGV uses the real thing; both reach the host as "Crashed".
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
out=${1:-$repo/dist/crash-linux}
if [ "${UMBRA_PID:-}" = "" ]; then
  json=$(mktemp); printf '{"general":{"logLevel":"verbose"}}' > "$json"
  "$here/headless.sh" -w "$repo/widgets/clock" -j "$json" -s 3 -- "$0" "$out"; code=$?
  rm -f "$json"; exit $code
fi

# ----- inside the session -----
mkdir -p "$out"
log="$UMBRA_DATA_DIR/umbra.log"
failed=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failed=1; fi; }
count() { grep -c "$1" "$log"; }
signal=${UMBRA_CRASH_SIGNAL:-KILL}
wait_for() { for _ in $(seq 1 180); do [ "$(count "$1")" -ge "$2" ] && return 0; sleep 0.5; done; return 1; }
# Only web processes that descend from OUR umbra: never another application's WebKit.
web_processes() {
  local queue=("$UMBRA_PID") pid child
  while [ ${#queue[@]} -gt 0 ]; do
    pid=${queue[0]}; queue=("${queue[@]:1}")
    for child in $(pgrep -P "$pid"); do
      queue+=("$child")
      [ "$(cat /proc/$child/comm 2>/dev/null)" = "WebKitWebProces" ] && [ -z "$(pgrep -P "$child")" ] && echo "$child"
    done
  done
}

wait_for '\[clock\] ready$' 1
check 'the clock is ready' '[ "$(count "\[clock\] ready$")" -ge 1 ]'
for n in 1 2 3; do
  pids=$(web_processes)
  check "crash $n: found the widget's web process" '[ -n "$pids" ]'
  kill -$signal $pids 2>/dev/null
  wait_for "reloading widget ($n/3)" 1
  check "crash $n: the widget reloads ($n/3)" '[ "$(count "reloading widget ($n/3)")" = 1 ]'
  wait_for '\[clock\] ready$' $((n + 1))
  check "crash $n: the widget is ready again" '[ "$(count "\[clock\] ready$")" -ge $((n + 1)) ]'
done
kill -$signal $(web_processes) 2>/dev/null
wait_for 'widget keeps failing' 1
check 'crash 4: the host stops reloading' '[ "$(count "widget keeps failing")" = 1 ]'
sleep 2
$UMBRACTL capture clock "$out/stopped.png" >/dev/null 2>&1
check 'the stopped page can be captured' '[ -s "$out/stopped.png" ]'
check 'privacy counts 4 crashes and says the widget stopped' '$UMBRACTL privacy clock | tr -d " \n" | grep -q "\"crashes\":{\"count\":4,\"stopped\":true}"'

# A real click on the Reload button (centred, in the lower half of the page).
ready_before=$(count '\[clock\] ready$')
w=$(xdotool search --name "Umbra - Clock" | head -1)
eval "$(xdotool getwindowgeometry --shell "$w")"
for dy in 62 68 74 56 80; do
  xdotool mousemove $((X + WIDTH / 2)) $((Y + HEIGHT * dy / 100)) click 1
  sleep 1.5
  [ "$(count '\[clock\] ready$')" -gt "$ready_before" ] && break
done
check 'Reload on the stopped page brings the widget back' '[ "$(count "\[clock\] ready$")" -gt "$ready_before" ]'
$UMBRACTL capture clock "$out/reloaded.png" >/dev/null 2>&1

[ $failed = 0 ] && echo "CRASH TEST PASSED" || echo "CRASH TEST FAILED"
exit $failed
