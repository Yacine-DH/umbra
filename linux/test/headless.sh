#!/usr/bin/env bash
# Runs the development build of the Linux host in a throwaway session: Xvfb + openbox + its own D-Bus session, a fresh
# data folder (UMBRA_DATA_DIR) and a fake xdg-open that only records what it was asked to open. Nothing touches the real
# desktop or an installed Umbra, so screenshots taken INSIDE this session are fine (CLAUDE.md, "Linux screenshots").
#
#   linux/test/headless.sh [-w widget-folder]... [-j settings.json] [-s seconds] -- command [args...]
#
# Each -w folder is copied into the profile as an installed community widget; -j starts from that settings.json. After Umbra has run for -s seconds
# (default 5) the command runs inside the session with:
#   UMBRA_DATA_DIR   the throwaway profile          UMBRA_PID     the host's process id
#   UMBRACTL         "dotnet <umbractl.dll>"        UMBRA_OPENED  file listing the URLs "opened in the browser"
# Its exit code is the script's; when it fails, the end of umbra.log and of the host's output are printed.
# UMBRA_KEEP=1 keeps the profile folder (its path is printed) for a closer look. Needs: xvfb, openbox, dbus (dbus-run-session), a built src/Umbra.Linux and src/Umbra.Cli.
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
config=${UMBRA_CONFIGURATION:-Debug}
bin="$repo/src/Umbra.Linux/bin/$config/net10.0/umbra"
ctl="$repo/src/Umbra.Cli/bin/$config/net10.0/umbractl.dll"
widgets=(); seconds=5; settings=""
while [ $# -gt 0 ]; do
  case "$1" in
    -w) widgets+=("$2"); shift 2 ;;
    -s) seconds=$2; shift 2 ;;
    -j) settings=$2; shift 2 ;;
    --) shift; break ;;
    *) echo "usage: headless.sh [-w widget-folder]... [-j settings.json] [-s seconds] -- command [args...]" >&2; exit 2 ;;
  esac
done
[ $# -gt 0 ] || { echo "headless.sh: no command given" >&2; exit 2; }
[ -x "$bin" ] || { echo "headless.sh: $bin not found; run: dotnet build Umbra.Linux.slnf" >&2; exit 2; }
for tool in xvfb-run openbox dbus-run-session; do
  command -v "$tool" >/dev/null || { echo "headless.sh: $tool is not installed" >&2; exit 2; }
done

profile=$(mktemp -d "${UMBRA_TEST_TMP:-${TMPDIR:-/tmp}}/umbra-test-XXXXXX")
cleanup() { if [ "${UMBRA_KEEP:-}" = 1 ]; then echo "profile kept: $profile" >&2; else rm -rf "$profile"; fi; }
trap cleanup EXIT
mkdir -p "$profile/widgets" "$profile/bin"
for w in "${widgets[@]}"; do cp -r "$w" "$profile/widgets/$(basename "$w")"; done
[ -n "$settings" ] && cp "$settings" "$profile/settings.json"
# A stand-in for the browser: "opening a link" appends the URL to a file instead of starting anything.
printf '#!/bin/sh\nprintf "%%s\\n" "$1" >> "%s"\n' "$profile/opened.txt" > "$profile/bin/xdg-open"
chmod +x "$profile/bin/xdg-open"
: > "$profile/opened.txt"

export UMBRA_DATA_DIR="$profile" UMBRA_OPENED="$profile/opened.txt" UMBRACTL="dotnet $ctl"
export PATH="$profile/bin:$PATH" GDK_BACKEND=x11 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export UMBRA_BIN="$bin" UMBRA_WAIT="$seconds"
unset WAYLAND_DISPLAY

xvfb-run -a -s "-screen 0 1920x1080x24" dbus-run-session -- bash -c '
  openbox >/dev/null 2>&1 &
  sleep 0.5
  "$UMBRA_BIN" > "$UMBRA_DATA_DIR/stdout.txt" 2>&1 &
  export UMBRA_PID=$!
  sleep "$UMBRA_WAIT"
  "$@"
  code=$?
  kill "$UMBRA_PID" 2>/dev/null
  wait "$UMBRA_PID" 2>/dev/null
  exit $code
' headless "$@" 2>"$profile/session.txt"
code=$?
if [ $code -ne 0 ]; then
  echo "--- umbra.log (last 40 lines)" >&2; tail -n 40 "$profile/umbra.log" >&2 2>/dev/null
  echo "--- host output (last 20 lines)" >&2; tail -n 20 "$profile/stdout.txt" >&2 2>/dev/null
fi
exit $code
