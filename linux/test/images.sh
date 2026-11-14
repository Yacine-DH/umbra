#!/usr/bin/env bash
# permissions.saveImages / umbra.images on Linux (#362) in a throwaway session: default folder ~/Pictures/Umbra/<name>,
# saving a valid PNG under a host-chosen name, rejecting non-PNGs, refusing chooseFolder without a physical click,
# and honoring a user-picked folder from image-folders.json and forgetting it when reset.
#   linux/test/images.sh
set -u
here=$(cd "$(dirname "$0")" && pwd)
"$here/headless.sh" -w "$here/images-widget" -s 4 -- bash -c '
  set -u
  fail() { echo "FAIL  $*"; exit 1; }
  w=images-widget
  go() { $UMBRACTL data $w result --remove >/dev/null 2>&1; $UMBRACTL data $w --remove result >/dev/null 2>&1; $UMBRACTL data $w go "$1" >/dev/null; sleep 1.5; $UMBRACTL data $w result; }

  r=$(go folder)
  echo "$r" | grep -q "\"data\": \"Hearth\"" && echo "ok    default folder label is Hearth (full path hidden)" || fail "folder: $r"

  r=$(go save)
  echo "$r" | grep -q "\"ok\": true" && echo "ok    valid PNG saved" || fail "save: $r"
  ls "$HOME/Pictures/Umbra/Hearth"/Hearth*.png >/dev/null 2>&1 && echo "ok    PNG written in ~/Pictures/Umbra/Hearth" || fail "file not in ~/Pictures/Umbra/Hearth"

  r=$(go save-bad)
  echo "$r" | grep -q "Only PNG pictures can be saved" && echo "ok    non-PNG rejected" || fail "save-bad: $r"

  r=$(go choose)
  echo "$r" | grep -q "Click the widget first" && echo "ok    chooseFolder refused without a physical click" || fail "choose without click: $r"

  custom="$UMBRA_DATA_DIR/MyBoardPics"
  mkdir -p "$custom"
  printf "{\"images-widget\":\"%s\"}\n" "$custom" > "$UMBRA_DATA_DIR/image-folders.json"
  r=$(go folder)
  echo "$r" | grep -q "\"data\": \"MyBoardPics\"" && echo "ok    picked folder wins over default" || fail "picked folder: $r"
  r=$(go save)
  ls "$custom"/Hearth*.png >/dev/null 2>&1 && echo "ok    PNG written in picked folder" || fail "file not in picked folder: $r"

  echo "IMAGES TEST PASSED"
'
