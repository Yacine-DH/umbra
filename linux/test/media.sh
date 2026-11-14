#!/usr/bin/env bash
# umbra.media on Linux (#190) in a throwaway session with a fake MPRIS player: the now-playing answer (title, artists,
# album, app, controls, file:// art), control refused without a click, accepted right after a real click, and refused for a
# widget without media "control". Also captures the catalog now-playing widget showing the fake song.
#   linux/test/media.sh      Needs what headless.sh needs, plus python3-gi.
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
"$here/headless.sh" -w "$here/media-widget" -w "$repo/widgets/now-playing" -s 4 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  calls="$UMBRA_DATA_DIR/mpris.txt"; : > "$calls"
  art="$UMBRA_DATA_DIR/art.png"
  cp '"$repo"'/linux/icons/hicolor/64x64/apps/io.github.aminechr54.Umbra.png "$art"
  python3 '"$here"'/fake-mpris.py "$calls" "$art" & ppid=$!
  trap "kill $ppid 2>/dev/null" EXIT
  sleep 1
  w=media-widget
  go() { $UMBRACTL data $w result --remove >/dev/null 2>&1; $UMBRACTL data $w --remove result >/dev/null 2>&1; $UMBRACTL data $w go "$1" >/dev/null; sleep 1.5; $UMBRACTL data $w result; }
  r=$(go get); echo "      $r" | tr -d "\n" | cut -c1-400; echo
  echo "$r" | grep -q "\"title\": \"Test Song\"" && echo "ok    title" || fail "title: $r"
  echo "$r" | grep -q "\"artist\": \"Artist A, Artist B\"" && echo "ok    artists joined" || fail "artist"
  echo "$r" | grep -q "\"app\": \"Umbra Test Player\"" && echo "ok    app is the player identity" || fail "app"
  echo "$r" | grep -q "\"playing\": true" && echo "ok    playing" || fail "playing"
  echo "$r" | grep -q "\"canPrevious\": false" && echo "ok    Can* mapped" || fail "can*"
  echo "$r" | grep -q "data:image/png;base64" && echo "ok    file:// art as a data URL" || fail "art"
  python3 -c "import os, zlib, struct
raw = b\"\".join(b\"\x00\" + os.urandom(800*4) for _ in range(600))
chunk = lambda t, d: struct.pack(\">I\", len(d)) + t + d + struct.pack(\">I\", zlib.crc32(t + d) & 0xffffffff)
open(\"$art\", \"wb\").write(b\"\x89PNG\r\n\x1a\n\" + chunk(b\"IHDR\", struct.pack(\">IIBBBBB\", 800, 600, 8, 6, 0, 0, 0)) + chunk(b\"IDAT\", zlib.compress(raw, 0)) + chunk(b\"IEND\", b\"\"))"
  r=$(go get)
  echo "$r" | grep -q "data:image/jpeg;base64" && echo "ok    large (>512 KB) file:// art scaled down to JPEG" || fail "large art not scaled: $(echo "$r" | cut -c1-200)"
  r=$(go toggle); echo "$r" | grep -q "Click the widget first" && echo "ok    control refused without a click" || fail "control without click: $r"
  grep -q PlayPause "$calls" && fail "the player got a command without a click"
  xdotool mousemove 450 350 click 1; sleep 1.5
  grep -q "^Next$" "$calls" && echo "ok    a real click lets the widget skip to the next track" || fail "Next not sent after a click: $($UMBRACTL data $w result)"
  $UMBRACTL capture now-playing "$UMBRA_DATA_DIR/np.png" >/dev/null && [ -n "${MEDIA_SHOT:-}" ] && cp "$UMBRA_DATA_DIR/np.png" "$MEDIA_SHOT"
  echo "MEDIA TEST PASSED"
'
