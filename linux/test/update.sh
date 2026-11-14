#!/usr/bin/env bash
# The notify-only update check on Linux (#197) in a headless session, against a fake release feed (UMBRA_RELEASE_URL, a
# Debug-only hook; no network): the automatic check finds a newer release, the tray turns to NeedsAttention with an item
# that opens the release page (and nothing is downloaded), Settings > About says so, and an older feed announces nothing.
#   linux/test/update.sh [out.png]     Needs what tray.sh needs. Writes the About capture to out.png (default: a temp file).
set -u
here=$(cd "$(dirname "$0")" && pwd)
feeds=$(mktemp -d)
trap 'rm -rf "$feeds"' EXIT
feed() { cat > "$feeds/$1.json" <<EOF
{ "tag_name": "v$1", "draft": false, "prerelease": false, "body": "Test release",
  "assets": [ { "name": "UmbraSetup-$1.exe", "browser_download_url": "https://github.com/AmineChr54/umbra/releases/download/v$1/UmbraSetup-$1.exe" },
              { "name": "umbra-$1-linux-x64.tar.gz", "browser_download_url": "https://github.com/AmineChr54/umbra/releases/download/v$1/umbra-$1-linux-x64.tar.gz" },
              { "name": "umbra-$1-linux-arm64.tar.gz", "browser_download_url": "https://github.com/AmineChr54/umbra/releases/download/v$1/umbra-$1-linux-arm64.tar.gz" } ] }
EOF
}
feed 99.0.0
feed 0.1.0
export UMBRA_SHOT=${1:-$feeds/about.png}

# 1. A newer release: tray attention, menu item, release page, About capture. The first automatic check runs 8 s after start.
UMBRA_RELEASE_URL="$feeds/99.0.0.json" "$here/headless.sh" -w "$here/edit-widget" -s 11 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  grep -q "Update available: 99.0.0" "$UMBRA_DATA_DIR/umbra.log" && echo "ok    the automatic check found 99.0.0" || fail "no automatic check in the log"
  $UMBRACTL check-update | grep -q "99.0.0" && echo "ok    umbractl check-update reports 99.0.0" || fail "check-update: $($UMBRACTL check-update)"
  reg="$UMBRA_DATA_DIR/watcher.txt"; : > "$reg"
  python3 '"$here"'/fake-sni-watcher.py "$reg" & wpid=$!
  trap "kill $wpid 2>/dev/null" EXIT
  for i in $(seq 1 50); do [ -s "$reg" ] && break; sleep 0.2; done
  read -r sender service < "$reg" || fail "Umbra did not register its tray icon"
  prop() { gdbus call --session -d "$service" -o /StatusNotifierItem -m org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierItem "$1"; }
  prop Status | grep -q NeedsAttention && echo "ok    tray status NeedsAttention" || fail "Status: $(prop Status)"
  prop AttentionIconName | grep -q attention && echo "ok    attention icon $(prop AttentionIconName)" || fail "AttentionIconName: $(prop AttentionIconName)"
  layout=$(gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.GetLayout -- 0 -1 "[]")
  id=$(echo "$layout" | grep -oP "\(\K[0-9]+(?=, \{[^}]*'"'"'label'"'"': <'"'"'Download Umbra v99.0.0)" | head -1)
  [ -n "$id" ] && echo "ok    the menu offers Umbra v99.0.0" || fail "no update item: $layout"
  gdbus call --session -d "$service" -o /MenuBar -m com.canonical.dbusmenu.Event "$id" clicked "<0>" 0 >/dev/null
  for i in $(seq 1 25); do grep -q "releases/tag/v99.0.0" "$UMBRA_OPENED" 2>/dev/null && break; sleep 0.2; done
  grep -qx "https://github.com/AmineChr54/umbra/releases/tag/v99.0.0" "$UMBRA_OPENED" && echo "ok    the item opens the release page" || fail "opened: $(cat "$UMBRA_OPENED" 2>/dev/null)"
  find "$UMBRA_DATA_DIR" -name "*.tar.gz" | grep -q . && fail "something was downloaded" || echo "ok    nothing downloaded"
  $UMBRACTL capture settings:about "$UMBRA_SHOT" >/dev/null && [ -s "$UMBRA_SHOT" ] && echo "ok    Settings > About captured: $UMBRA_SHOT" || fail "capture"
' || exit 1

# 2. An older release: no notice, no attention.
UMBRA_RELEASE_URL="$feeds/0.1.0.json" "$here/headless.sh" -w "$here/edit-widget" -s 2 -- bash -c '
  fail() { echo "FAIL  $*"; exit 1; }
  $UMBRACTL check-update >/dev/null
  grep -q "Update available" "$UMBRA_DATA_DIR/umbra.log" && fail "an older release was announced" || echo "ok    an older release is not announced"
' || exit 1
echo "update.sh: all passed"
