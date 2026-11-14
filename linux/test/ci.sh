#!/usr/bin/env bash
# The headless part of the Linux CI job (#198): builds the Linux host (Debug, warnings are errors) and runs, each in its
# own throwaway session and profile:
#   smoke     linux/test/smoke.sh under Xvfb + openbox (X11 path): umbractl end to end, captures
#   sandbox   linux/test/sandbox.sh: the hostile widget; fails as soon as one undeclared request escapes
#   catalog   linux/test/catalog.sh: every catalog widget loads, and the sandbox blocks nothing they need
#   wayland   linux/test/gnome.sh under headless GNOME Shell (native Wayland + the extension). Optional: a failure is
#             reported but not counted unless UMBRA_CI_WAYLAND=1. SKIPPED with a reason when gnome-shell isn't installed
#             (the CI container doesn't install it: a headless shell in an unprivileged container is untested) or UMBRA_CI_WAYLAND=0
# Captures, every run's umbra.log and each step's output land in out-dir (default dist/ci/<sha7>/linux), like the Windows
# smoke job. Exit 1 if any step failed. scripts/ci-local.ps1 runs this inside its Linux Docker job; it runs the same here.
#
#   linux/test/ci.sh [--offline] [--no-build] [out-dir]
#
# --offline: no network (skips the catalog install in smoke and the two declared-host checks in sandbox). The update check
# never reaches GitHub either way: UMBRA_RELEASE_URL points at a local feed (a Debug-only hook, hence the Debug build).
# --no-build: use the existing Debug build of src/Umbra.Linux and src/Umbra.Cli (ci-local.ps1 builds them before).
# Each step is killed after UMBRA_CI_STEP_TIMEOUT seconds (default 900) and then counts as failed ("timed out").
# Needs what headless.sh, sandbox.sh and catalog.sh need: xvfb (xvfb-run, xauth), openbox, dbus, python3, xdotool.
set -u
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
offline=0; build=1; out=""
for a in "$@"; do
  case "$a" in
    --offline) offline=1 ;;
    --no-build) build=0 ;;
    -*) echo "usage: ci.sh [--offline] [--no-build] [out-dir]" >&2; exit 2 ;;
    *) out=$a ;;
  esac
done
sha=${UMBRA_CI_SHA:-$(git -C "$repo" rev-parse --short=7 HEAD 2>/dev/null || echo local)}
[ -n "$out" ] || out="$repo/dist/ci/$sha/linux"
mkdir -p "$out"; out=$(cd "$out" && pwd)
export UMBRA_CONFIGURATION=Debug DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
work=$(mktemp -d "${TMPDIR:-/tmp}/umbra-ci-XXXXXX")
trap 'rm -rf "$work"' EXIT
export UMBRA_TEST_TMP="$work"
results=(); failed=0
step_timeout=${UMBRA_CI_STEP_TIMEOUT:-900} # seconds per step
echo "Umbra Linux CI ($sha), output in $out"

if [ $build = 1 ]; then
  echo "== build (Debug, -warnaserror)"
  if dotnet build "$repo/Umbra.Linux.slnf" -warnaserror > "$out/build.log" 2>&1; then
    grep -E 'Warning\(s\)|Error\(s\)' "$out/build.log" | sed 's/^ */   /'
  else
    grep -E ' error |Warning\(s\)|Error\(s\)' "$out/build.log" | tail -n 20
    echo "FAIL  build (see $out/build.log)" | tee "$out/summary.txt"; echo "Linux CI FAILED"; exit 1
  fi
fi

# A local release feed older than any build: the automatic check runs and finds nothing, without any network.
printf '{ "tag_name": "v0.0.1", "draft": false, "prerelease": false, "body": "CI feed", "assets": [] }\n' > "$work/release.json"
export UMBRA_RELEASE_URL="$work/release.json"

# WebKitGTK puts every web process in a bubblewrap sandbox. An unprivileged Docker container can't create the namespaces
# it needs; rather than give the container privileges (it runs PR code), switch WebKit's own process sandbox off there.
# Umbra's widget sandbox (web-process extension, per-widget CSP, dead-end proxy) is separate and stays on: sandbox.sh tests it.
# Only inside a container (never on a dev machine, where bwrap works or the run should show that it doesn't).
# Coverage gap: in the container the web-process extension is therefore never loaded under bubblewrap, which is how users
# run it. A native run of this script on a real Linux machine (bwrap working) covers that combination.
if ! command -v bwrap >/dev/null || ! bwrap --ro-bind / / --unshare-all --die-with-parent true 2>/dev/null; then
  if [ -f /.dockerenv ] || [ "${UMBRA_CI_CONTAINER:-}" = 1 ]; then
    echo "note: bubblewrap can't create namespaces in this container: WebKit's process sandbox is off (WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1)"
    export WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1
  else
    echo "note: bubblewrap can't create namespaces here; WebKit may fail to start its web processes"
  fi
fi

# run [-o] <name> <command...>: output to <name>.txt, the profile's umbra.log (and host output) to <name>-umbra.log.
# -o marks an optional step: a failure is reported but doesn't fail the job.
run() {
  local optional=0; [ "$1" = -o ] && { optional=1; shift; }
  local name=$1; shift
  echo "== $name"
  local start=$SECONDS
  # A hung step must not block CI: timeout signals its whole process group (Xvfb, the shell, umbra), then SIGKILL.
  UMBRA_KEEP=1 timeout -k 10 "$step_timeout" "$@" > "$out/$name.txt" 2> "$out/$name.err"
  local code=$? note=""
  [ $code = 124 ] || [ $code = 137 ] && note="timed out after $step_timeout s, "
  # headless.sh / gnome.sh print "profile kept: <dir>" / "kept: <dir>": copy what's useful out of each, then delete it.
  local n=0
  while read -r dir; do
    [ -d "$dir" ] || continue
    local suffix=""; [ $n -gt 0 ] && suffix="-$n"
    for f in umbra.log profile/umbra.log; do [ -f "$dir/$f" ] && cp "$dir/$f" "$out/$name$suffix-umbra.log"; done
    for f in stdout.txt shell.log; do [ -f "$dir/$f" ] && cp "$dir/$f" "$out/$name$suffix-${f%.*}.log"; done
    case "$(basename "$dir")" in umbra-test-*|umbra-gnome-*) rm -rf "$dir" ;; esac
    n=$((n + 1))
  done < <(sed -n 's/^\(profile \)\{0,1\}kept: //p' "$out/$name.err")
  grep -Ev '^(profile )?kept: ' "$out/$name.err" >> "$out/$name.txt"; rm -f "$out/$name.err"
  grep -E '^ *(FAIL|SKIPPED|skip )' "$out/$name.txt" | head -n 15 | sed 's/^ */   /'
  if [ $code = 0 ]; then results+=("PASS  $name ($((SECONDS - start)) s)")
  elif [ $optional = 1 ]; then results+=("FAIL  $name (optional, not counted; ${note}exit $code; see $out/$name.txt)")
  else results+=("FAIL  $name (${note}exit $code, $((SECONDS - start)) s; see $out/$name.txt)"); failed=1; tail -n 25 "$out/$name.txt" | sed 's/^/   | /'; fi
  echo "   ${results[-1]}"
}

flag=(); [ $offline = 1 ] && flag=(--offline)
run smoke "$here/smoke.sh" "${flag[@]}" "$out/smoke"
run sandbox "$here/sandbox.sh" "${flag[@]}"
run catalog "$here/catalog.sh"
if [ "${UMBRA_CI_WAYLAND:-}" = 0 ]; then
  results+=("SKIPPED  wayland (UMBRA_CI_WAYLAND=0)"); echo "== wayland"; echo "   ${results[-1]}"
elif ! command -v gnome-shell >/dev/null || ! command -v gjs >/dev/null; then
  echo "== wayland"
  if [ "${UMBRA_CI_WAYLAND:-}" = 1 ]; then
    results+=("FAIL  wayland: UMBRA_CI_WAYLAND=1 but gnome-shell or gjs is not installed"); failed=1
  else
    results+=("SKIPPED  wayland: gnome-shell and gjs are not installed (install them to run linux/test/gnome.sh under headless GNOME Shell)")
  fi
  echo "   ${results[-1]}"
else
  mkdir -p "$out/wayland"
  # Optional unless UMBRA_CI_WAYLAND=1: the GNOME path isn't part of the required job (the container doesn't run it).
  opt=(-o); [ "${UMBRA_CI_WAYLAND:-}" = 1 ] && opt=()
  # TMPDIR: gnome.sh's temp folder goes under $work too, so even a timed-out run leaves nothing behind.
  TMPDIR="$work" UMBRA_OUT="$out/wayland" run "${opt[@]}" wayland "$here/gnome.sh"
fi

echo
printf '%s\n' "${results[@]}" | tee "$out/summary.txt"
if [ $failed = 1 ]; then echo "Linux CI FAILED"; exit 1; fi
echo "Linux CI passed."
