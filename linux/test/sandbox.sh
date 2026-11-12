#!/usr/bin/env bash
# The Linux sandbox test (#180): runs linux/test/hostile-widget in a throwaway headless session and fails if anything it
# tried reached an undeclared host. Needs network access for the two DECLARED hosts (api.github.com, raw.githubusercontent.com);
# pass --offline to skip those two checks.
#
#   dotnet build Umbra.Linux.slnf && linux/test/sandbox.sh
#
# How a leak is detected: every undeclared request targets port 47812 on this machine, where a listener counts TCP
# connections (a TLS handshake that fails still counts). The widget also reports what each vector saw, and the host's
# umbra.log must name the blocked requests. A real mouse click on the widget's link must reach the (fake) browser.
set -u
here=$(cd "$(dirname "$0")" && pwd)
offline=0; [ "${1:-}" = "--offline" ] && offline=1

if [ "${UMBRA_PID:-}" = "" ]; then
  command -v python3 >/dev/null || { echo "sandbox.sh: python3 is needed for the connection counter" >&2; exit 2; }
  command -v xdotool >/dev/null || { echo "sandbox.sh: xdotool is needed for the link click" >&2; exit 2; }
  exec "$here/headless.sh" -w "$here/hostile-widget" -s 1 -- "$0" "$@"
fi

# ----- inside the session -----
hits="$UMBRA_DATA_DIR/hits.txt"; : > "$hits"
python3 - "$hits" <<'PY' &
import socket, sys
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", 47812)); s.listen(64)
while True:
    c, _ = s.accept()
    c.settimeout(1)
    try: first = c.recv(200)
    except Exception: first = b""
    open(sys.argv[1], "a").write(repr(first[:120]) + "\n")
    c.close()
PY
counter=$!
trap 'kill $counter 2>/dev/null' EXIT

data="$UMBRA_DATA_DIR/data/hostile-widget.json"
for _ in $(seq 1 90); do
  grep -q '"survived"' "$data" 2>/dev/null && break
  sleep 0.5
done

# A real click in the middle of the widget: the link must open in the browser and the widget must stay on its page.
win=$(xdotool search --name "Umbra - Hostile widget" | head -1)
if [ -n "$win" ]; then
  eval "$(xdotool getwindowgeometry --shell "$win")"
  xdotool mousemove $((X + WIDTH / 2)) $((Y + HEIGHT / 2)) click 1
  sleep 1.5
fi
still=$($UMBRACTL list 2>/dev/null | grep -c '"hostile-widget"')

python3 - "$data" "$hits" "$UMBRA_DATA_DIR/umbra.log" "$UMBRA_OPENED" "$offline" "$still" <<'PY'
import json, sys
data, hits, log, opened, offline, still = sys.argv[1:7]
try: store = json.load(open(data))
except Exception as e: print(f"FAIL  no results from the widget ({e})"); sys.exit(1)
results, violations = store.get("results", {}), store.get("violations", [])
logtext = open(log).read() if log else ""
blocked_lines = [l for l in logtext.splitlines() if "sandbox blocked" in l]
failed = []

def check(ok, text):
    print(("ok    " if ok else "FAIL  ") + text)
    if not ok: failed.append(text)

for name in sorted(results):
    value = results[name]
    if name in ("survived",): continue
    if name.startswith("declared-"):
        if offline == "1": print(f"skip  {name}: {value} (offline)")
        else: check(value == "loaded", f"{name}: {value} (a declared host must work)")
        continue
    vector = name.rsplit("-", 1)[0]
    how = []
    if any(f"v={vector}" in l for l in blocked_lines): how.append("extension")
    if any(f"v={vector}" in v for v in violations): how.append("CSP")
    if value == "error": how.append("page saw an error")
    check(value != "loaded", f"{name}: {value}" + (f" [{', '.join(how)}]" if how else " [no signal; see the connection count]"))

survived = results.get("survived", "")
check(survived.startswith("umbra://community.umbra.local/hostile-widget/"), f"the page was never navigated away (now at {survived or 'unknown'})")
for nav in ("form", "location", "meta-refresh", "anchor-script-click", "window-open"):
    check(any(f"v={nav}" in l for l in blocked_lines) or any(f"v={nav}" in v for v in violations), f"navigation by {nav} was blocked and logged")
leaks = [l for l in open(hits).read().splitlines() if l.strip()]
check(not leaks, f"connections that reached the undeclared target: {len(leaks)}")
for l in leaks: print(f"        first bytes: {l}")
links = [l for l in open(opened).read().splitlines() if l.strip()]
check(links == ["https://example.org/umbra-link-test"], f"a real click opened exactly the link in the browser: {links}")
check(still != "0", "the widget is still open after the click")
print(f"\n{len(blocked_lines)} 'sandbox blocked' lines in umbra.log, {len(violations)} CSP violations reported by the page")
print("SANDBOX TEST " + ("FAILED: " + "; ".join(failed) if failed else "PASSED"))
sys.exit(1 if failed else 0)
PY
