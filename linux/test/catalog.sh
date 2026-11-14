#!/usr/bin/env bash
# Loads every catalog widget (widgets/*) in a throwaway headless session and checks that each page reached the host
# ("ready"), which needs the https://umbra.local rewrite, the scheme handler and the page policy to all let it through.
#
#   dotnet build Umbra.Linux.slnf && linux/test/catalog.sh
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
# Widgets whose "platforms" leaves out linux (#194) are skipped by the host, so they're not expected to load.
linux_ok() { python3 -c 'import json,sys; p=json.load(open(sys.argv[1])).get("platforms"); sys.exit(0 if not p or "linux" in p else 1)' "$1/widget.json"; }

if [ "${UMBRA_PID:-}" = "" ]; then
  args=()
  for d in "$repo"/widgets/*/; do [ -f "$d/widget.json" ] && linux_ok "$d" && args+=(-w "${d%/}"); done
  json=$(mktemp); printf '{"general":{"logLevel":"verbose"}}' > "$json"
  UMBRA_WEB_CONSOLE=1 "$here/headless.sh" "${args[@]}" -j "$json" -s 2 -- "$0"; code=$?
  rm -f "$json"; exit $code
fi

# ----- inside the session -----
ids=$(for d in "$repo"/widgets/*/; do [ -f "$d/widget.json" ] && linux_ok "$d" && basename "$d"; done)
total=$(echo "$ids" | wc -l)
for _ in $(seq 1 60); do
  [ "$(grep -c '\] ready$' "$UMBRA_DATA_DIR/umbra.log")" -ge "$total" ] && break
  sleep 0.5
done
sleep 2 # let late requests show up in the log
failed=0
for id in $ids; do
  if grep -q "\[$id\] ready\$" "$UMBRA_DATA_DIR/umbra.log"; then echo "ok    $id"; else echo "FAIL  $id never became ready"; failed=1; fi
done
echo "--- requests the sandbox blocked (expected: none for catalog widgets)"
grep "sandbox blocked" "$UMBRA_DATA_DIR/umbra.log" | sed 's/^[0-9: -]*//' | sort | uniq -c
echo "--- page console errors"
grep -a "CONSOLE.*ERROR\|Refused to" "$UMBRA_DATA_DIR/stdout.txt" | cut -c1-240 | sort | uniq -c | head -40
grep -q "sandbox blocked" "$UMBRA_DATA_DIR/umbra.log" && failed=1
[ $failed = 0 ] && echo "CATALOG TEST PASSED ($total widgets)" || echo "CATALOG TEST FAILED"
exit $failed
