#!/usr/bin/env bash
# End-to-end smoke test of the Linux host, the counterpart of scripts/smoke.ps1: starts a real `umbra` in a throwaway
# headless session (linux/test/headless.sh) and drives it with umbractl: status, list, new + capture, configure, data,
# MCP (tools list + capture), catalog install/remove, commands that are not on Linux yet, logs, quit.
#
#   dotnet build Umbra.Linux.slnf && linux/test/smoke.sh [--offline] [out-dir]
#
# --offline skips the catalog install (it downloads from GitHub). Captures and umbra.log are copied to out-dir
# (default dist/smoke-linux). Also runs in a nested GNOME session: start one, then run this script inside it with
# UMBRA_PID set to a running dev instance's pid and UMBRA_DATA_DIR / UMBRACTL exported as headless.sh does.
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
offline=0; out="$repo/dist/smoke-linux"
for a in "$@"; do case "$a" in --offline) offline=1 ;; *) out=$a ;; esac; done

if [ "${UMBRA_PID:-}" = "" ]; then
  mkdir -p "$out"
  exec "$here/headless.sh" -s 1 -- "$0" "$@"
fi

# ----- inside the session -----
mkdir -p "$out"
failures=()
step() { echo "== $1"; }
pass() { echo "   ok: $1"; }
fail() { echo "   FAIL: $1"; failures+=("$1"); }
skip() { echo "   SKIPPED: $1"; }
ctl() { ctl_out=$($UMBRACTL "$@" 2>&1); ctl_code=$?; }
is_png() { [ -f "$1" ] && [ "$(stat -c %s "$1")" -gt 1000 ] && [ "$(head -c 4 "$1" | od -An -tx1 | tr -d ' \n')" = "89504e47" ]; }
has_id() { $UMBRACTL list 2>/dev/null | python3 -c 'import json,sys; sys.exit(0 if any(w["id"]==sys.argv[1] for w in json.load(sys.stdin)) else 1)' "$1"; }

step 'status'
for _ in $(seq 1 120); do ctl status; [ $ctl_code = 0 ] && break; sleep 0.5; done
if [ $ctl_code = 0 ]; then pass "status answered, version $(echo "$ctl_out" | python3 -c 'import json,sys; print(json.load(sys.stdin)["version"])')"
else fail "umbra did not answer status: $ctl_out"; fi

step 'second launch'
timeout 15 "$UMBRA_BIN" >/dev/null 2>&1; code=$?
if [ $code = 0 ] && kill -0 "$UMBRA_PID" 2>/dev/null; then pass 'a second umbra exited and the first keeps running'
else fail "second launch: exit $code, first instance alive: $(kill -0 "$UMBRA_PID" 2>/dev/null && echo yes || echo no)"; fi

step 'list'
ctl list
if [ $ctl_code = 0 ] && [ "${ctl_out:0:1}" = "[" ]; then pass 'list returned an array'; else fail "list: $ctl_out"; fi

step 'new + list'
id=smoke-widget
ctl new $id
if [ $ctl_code = 0 ] && has_id $id; then pass "$id loaded"; else fail "new $id (exit $ctl_code): $ctl_out"; fi

step 'capture'
png="$out/$id.png"; rm -f "$png"
ctl capture $id "$png"
if [ $ctl_code != 0 ]; then fail "capture exited $ctl_code: $ctl_out"
elif is_png "$png"; then pass "$png ($(stat -c %s "$png") bytes)"
else fail 'capture is not a usable PNG'; fi

step 'configure'
ctl configure $id --scale 1.5
scale=$($UMBRACTL list | python3 -c 'import json,sys; print([w["scale"] for w in json.load(sys.stdin) if w["id"]==sys.argv[1]][0])' $id 2>/dev/null)
if [ $ctl_code = 0 ] && [ "$scale" = "1.5" ]; then pass 'scale 1.5 applied'; else fail "scale is '$scale' (exit $ctl_code): $ctl_out"; fi
ctl configure $id --scale 4
[ $ctl_code = 1 ] && pass 'scale 4 rejected' || fail "scale 4 should exit 1, got $ctl_code"
ctl configure $id --disable; ctl configure $id --enable
if [ $ctl_code = 0 ] && has_id $id; then pass 'disabled and enabled again'; else fail "enable/disable: $ctl_out"; fi

step 'data'
ctl data $id count 42
ctl data $id count
if [ $ctl_code = 0 ] && echo "$ctl_out" | grep -q 42; then pass 'stored and read back 42'; else fail "data: $ctl_out"; fi
ctl data $id --clear

step 'reload'
ctl reload
[ $ctl_code = 0 ] && has_id $id && pass 'reload kept the widget' || fail "reload: $ctl_out"

step 'mcp'
mcp=$(printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"capture\",\"arguments\":{\"target\":\"$id\"}}}" \
  | timeout 60 $UMBRACTL mcp 2>/dev/null)
echo "$mcp" | python3 -c '
import base64, json, sys
tools = image = None
for line in sys.stdin:
    line = line.strip()
    if not line: continue
    m = json.loads(line)
    if m.get("id") == 2: tools = [t["name"] for t in m["result"]["tools"]]
    if m.get("id") == 3:
        for c in m["result"].get("content", []):
            if c.get("type") == "image": image = base64.b64decode(c["data"])
ok = tools and "capture" in tools and image and image[:4] == b"\x89PNG" and len(image) > 1000
print(f"{len(tools or [])} tools, capture image {len(image or b"")} bytes")
sys.exit(0 if ok else 1)' > "$out/mcp.txt" 2>&1
[ $? = 0 ] && pass "$(cat "$out/mcp.txt")" || fail "mcp: $(cat "$out/mcp.txt") $(echo "$mcp" | head -c 300)"

step 'catalog install + remove'
if [ $offline = 1 ]; then skip 'offline'
else
  ctl install clock
  if [ $ctl_code = 0 ] && has_id clock; then
    pass 'clock installed from the catalog'
    ctl capture clock "$out/clock.png"
    is_png "$out/clock.png" && pass "clock captured ($(stat -c %s "$out/clock.png") bytes)" || fail "clock capture: $ctl_out"
    ctl remove clock
    if [ $ctl_code = 0 ] && ! has_id clock; then pass 'clock removed'; else fail "remove clock: $ctl_out"; fi
  else fail "install clock (exit $ctl_code): $ctl_out"; fi
fi

step 'not on Linux yet: a clear message, not a crash'
ctl capture settings:home "$out/settings.png"
if [ $ctl_code != 0 ] && echo "$ctl_out" | grep -qi "not available on Linux yet"; then pass "settings capture: $ctl_out"
else fail "settings capture should fail clearly, got exit $ctl_code: $ctl_out"; fi
ctl settings general
if [ $ctl_code != 0 ] && echo "$ctl_out" | grep -qi "not available on Linux yet"; then pass "settings: $ctl_out"
else fail "umbractl settings should fail clearly, got exit $ctl_code: $ctl_out"; fi
ctl status
[ $ctl_code = 0 ] && pass 'umbra still answers' || fail 'umbra stopped answering after the unsupported commands'

step 'logs'
ctl logs 500
bad=$(echo "$ctl_out" | grep -Ei 'exception|Fatal|failed|unreadable|Pipe error|Skipping widget' | grep -v "not available on Linux yet")
if [ $ctl_code = 0 ] && [ -z "$bad" ]; then pass 'no errors in the log'; else fail "log has errors: $bad"; fi

step 'remove'
ctl remove $id
if [ $ctl_code = 0 ] && ! has_id $id; then pass "$id removed"; else fail "remove: $ctl_out"; fi

step 'quit'
ctl quit
for _ in $(seq 1 30); do kill -0 "$UMBRA_PID" 2>/dev/null || break; sleep 0.5; done
kill -0 "$UMBRA_PID" 2>/dev/null && fail 'umbra did not exit after quit' || pass 'umbra exited'
cp "$UMBRA_DATA_DIR/umbra.log" "$out/umbra.log" 2>/dev/null

if [ ${#failures[@]} -gt 0 ]; then
  printf '\nSmoke test FAILED (%d):\n' ${#failures[@]}; printf ' - %s\n' "${failures[@]}"; exit 1
fi
printf '\nSmoke test passed.\n'
