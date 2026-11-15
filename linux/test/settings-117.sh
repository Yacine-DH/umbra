#!/usr/bin/env bash
# Settings 1.17 parity on Linux (#366): Browse install counts, InstallStats.Enabled on fresh catalog installs,
# NoteCatalog + tray "Update n widgets" click, and all 12 spotlight welcome tour steps (settings:welcome:1..12).
#   linux/test/settings-117.sh
set -u
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

mkdir -p "$tmp/widgets/test-cat" "$tmp/widgets/fresh-cat" "$tmp/themes"
cat > "$tmp/widgets/test-cat/widget.json" <<'EOF'
{ "id": "test-cat", "name": "Test Cat", "version": "1.0.0", "author": "Test", "description": "Catalog test widget.", "width": 200, "height": 120 }
EOF
cat > "$tmp/widgets/test-cat/index.html" <<'EOF'
<!doctype html><html><body style="color:#fff;font-family:sans-serif">Test Cat 1.0.0</body></html>
EOF
cat > "$tmp/widgets/fresh-cat/widget.json" <<'EOF'
{ "id": "fresh-cat", "name": "Fresh Cat", "version": "1.0.0", "author": "Test", "description": "Fresh catalog widget.", "width": 200, "height": 120 }
EOF
cat > "$tmp/widgets/fresh-cat/index.html" <<'EOF'
<!doctype html><html><body style="color:#fff;font-family:sans-serif">Fresh Cat 1.0.0</body></html>
EOF
cat > "$tmp/widgets/index.json" <<'EOF'
{
  "widgets": [
    { "id": "test-cat", "name": "Test Cat", "version": "1.0.0", "author": "Test", "description": "Catalog test widget.", "category": "utility", "files": ["widget.json", "index.html"] },
    { "id": "fresh-cat", "name": "Fresh Cat", "version": "1.0.0", "author": "Test", "description": "Fresh catalog widget.", "category": "utility", "files": ["widget.json", "index.html"] }
  ]
}
EOF
cat > "$tmp/themes/index.json" <<'EOF'
{ "themes": [] }
EOF
cat > "$tmp/existing-settings.json" <<'EOF'
{ "general": { "tourDone": false, "countInstalls": true, "checkForUpdates": true } }
EOF

# Loopback stats relay: serves /stats/installs and records /stats/install?w=<id> requests.
python3 - "$tmp" <<'PY' &
import http.server, json, socketserver, sys
tmp = sys.argv[1]
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        with open(f"{tmp}/requests.txt", "a") as f:
            f.write(self.path + "\n")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        if self.path.startswith("/stats/installs"):
            self.wfile.write(json.dumps({"widgets": {"test-cat": 42, "fresh-cat": 7}}).encode())
        else:
            self.wfile.write(b'{"ok":true}')
with socketserver.TCPServer(("127.0.0.1", 0), Handler) as httpd:
    with open(f"{tmp}/port.txt", "w") as f:
        f.write(str(httpd.server_address[1]))
    httpd.serve_forever()
PY
spid=$!
trap 'kill $spid 2>/dev/null; rm -rf "$tmp"' EXIT
for _ in $(seq 1 50); do [ -s "$tmp/port.txt" ] && break; sleep 0.1; done
port=$(cat "$tmp/port.txt")

export UMBRA_CATALOG_BASE="file://$tmp/widgets"
export UMBRA_STATS_BASE="http://127.0.0.1:$port"
export UMBRA_TEST_TMP="$tmp"

UMBRA_ARGS="" "$here/headless.sh" -w "$here/edit-widget" -j "$tmp/existing-settings.json" -s 3 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  is_png() { [ -f "$1" ] && [ "$(stat -c %s "$1")" -gt 1000 ] && [ "$(head -c 4 "$1" | od -An -tx1 | tr -d " \n")" = "89504e47" ]; }

  # 0. Existing profile with tourDone=false on a normal start: Tour.Decide returns Offer, marks tourDone=true, and opens Settings.
  grep -q "\"tourDone\": true" "$UMBRA_DATA_DIR/settings.json" && echo "ok    existing profile offered welcome tour once (tourDone saved)" || fail "tourDone was not set on existing profile"

  # Seed an outdated community widget (0.9.0 vs 1.0.0 in catalog).
  mkdir -p "$UMBRA_DATA_DIR/widgets/test-cat"
  cat > "$UMBRA_DATA_DIR/widgets/test-cat/widget.json" <<EOF
{ "id": "test-cat", "name": "Test Cat", "version": "0.9.0", "author": "Test", "description": "Catalog test widget.", "width": 200, "height": 120 }
EOF
  cp "$UMBRA_TEST_TMP/widgets/test-cat/index.html" "$UMBRA_DATA_DIR/widgets/test-cat/index.html"
  $UMBRACTL reload >/dev/null || fail "reload"

  # Register a fake StatusNotifierWatcher so the tray menu is live.
  reg="$UMBRA_DATA_DIR/watcher.txt"; : > "$reg"
  python3 '"$here"'/fake-sni-watcher.py "$reg" & wpid=$!
  trap "kill $wpid 2>/dev/null" EXIT
  for _ in $(seq 1 50); do [ -s "$reg" ] && break; sleep 0.2; done
  read -r _ service < "$reg" || fail "tray did not register"

  # 1. Capture Browse: fetches catalog + install counts and calls NoteCatalog -> tray shows "Update 1 widget".
  $UMBRACTL capture settings:browse "$UMBRA_TEST_TMP/browse.png" >/dev/null || fail "capture browse"
  is_png "$UMBRA_TEST_TMP/browse.png" && echo "ok    Settings > Browse captured" || fail "browse png"
  grep -q "^/stats/installs$" "$UMBRA_TEST_TMP/requests.txt" && echo "ok    Browse fetched /stats/installs" || fail "did not fetch /stats/installs"

  layout=$(gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.GetLayout -- 0 -1 "[]")
  id=$(echo "$layout" | grep -oP "\(\K[0-9]+(?=, \{[^}]*'"'"'label'"'"': <'"'"'Update 1 widget)" | head -1)
  [ -n "$id" ] && echo "ok    tray menu offers \"Update 1 widget\"" || fail "no Update 1 widget in tray: $layout"

  # 2. Click "Update 1 widget" in the tray menu: updates test-cat to 1.0.0 and clears the tray item.
  gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.Event "$id" clicked "<0>" 0 >/dev/null
  for _ in $(seq 1 50); do grep -q "Updated 1 widget(s) from the tray" "$UMBRA_DATA_DIR/umbra.log" && break; sleep 0.2; done
  grep -q "Updated 1 widget(s) from the tray" "$UMBRA_DATA_DIR/umbra.log" && echo "ok    clicking \"Update 1 widget\" updated test-cat to 1.0.0" || fail "tray update did not finish"
  grep -q "/stats/install?w=test-cat" "$UMBRA_TEST_TMP/requests.txt" && fail "update was counted as fresh install" || echo "ok    widget update is not counted as a fresh install"

  # 3. Fresh catalog install reports +1 when CountInstalls is on.
  $UMBRACTL install fresh-cat >/dev/null || fail "install fresh-cat"
  for _ in $(seq 1 30); do grep -q "^/stats/install?w=fresh-cat$" "$UMBRA_TEST_TMP/requests.txt" && break; sleep 0.1; done
  grep -q "^/stats/install?w=fresh-cat$" "$UMBRA_TEST_TMP/requests.txt" && echo "ok    fresh catalog install reported /stats/install?w=fresh-cat" || fail "fresh install not counted"

  # 4. All 12 spotlight welcome tour steps render and capture cleanly.
  for s in $(seq 1 12); do
    out="$UMBRA_TEST_TMP/welcome-$s.png"
    res=$($UMBRACTL capture "settings:welcome:$s" "$out" 2>&1) || fail "capture settings:welcome:$s: $res"
    is_png "$out" || fail "welcome:$s is not a valid PNG ($(stat -c %s "$out" 2>/dev/null) bytes)"
  done
  echo "ok    all 12 welcome tour steps (settings:welcome:1..12) captured"
  echo "SETTINGS 1.17 TEST PASSED"
'
