#!/usr/bin/env bash
# Builds the self-contained Linux tarball: dist/umbra-<version>-linux-<arch>.tar.gz with the app, umbractl, the app menu
# entry, the icons and install.sh. .NET is bundled; GTK 4 and WebKitGTK 6 come from the system.
#   linux/package.sh [x64|arm64]      (default x64). Needs the .NET 10 SDK, gcc and libwebkitgtk-6.0-dev.
# Cross-building arm64 on x64: set WEBEXT_CC=aarch64-linux-gnu-gcc and PKG_CONFIG_LIBDIR to the arm64 pkgconfig folders
# (linux/package.Dockerfile, target arm64, does). The sandbox extension's ELF machine must match the target or nothing is packaged.
set -eu
repo=$(cd "$(dirname "$0")/.." && pwd)
arch=${1:-x64}
case "$arch" in
  x64) machine=62 ;;    # EM_X86_64
  arm64) machine=183 ;; # EM_AARCH64
  *) echo "unknown arch '$arch' (x64 or arm64)" >&2; exit 2 ;;
esac
ver=$(grep -oP '(?<=<Version>)[^<]+' "$repo/Directory.Build.props")
name="umbra-$ver-linux-$arch"
out="$repo/dist/$name"
rm -rf "$out"
cc=()
[ -n "${WEBEXT_CC:-}" ] && cc=("-p:WebExtCC=$WEBEXT_CC")
dotnet publish "$repo/src/Umbra.Linux" -c Release -r "linux-$arch" --self-contained true -p:DebugType=none "${cc[@]}" -o "$out/umbra" --nologo -v q
dotnet publish "$repo/src/Umbra.Cli" -c Release -r "linux-$arch" --self-contained true -p:DebugType=none -o "$out/umbractl" --nologo -v q
so="$out/umbra/webext/libumbra-webext.so"
[ -f "$so" ] || { echo "the sandbox extension is missing from the build" >&2; exit 1; }
# An extension for another architecture would never load and the first sandbox layer would be gone (the host refuses it too).
got=$(od -An -tu2 -j18 -N2 "$so" | tr -d ' ')
[ "$(head -c 4 "$so" | od -An -tx1 | tr -d ' \n')" = "7f454c46" ] && [ "$got" = "$machine" ] || {
  echo "the sandbox extension is not a linux-$arch library (ELF machine ${got:-?}, expected $machine): set WEBEXT_CC for a cross build" >&2
  rm -rf "$out"; exit 1; }
echo "$ver" > "$out/umbra/VERSION"
cp "$repo/linux/install.sh" "$repo/linux/io.github.aminechr54.Umbra.desktop" "$out/"
cp "$repo/LICENSE" "$out/" 2>/dev/null || true
chmod +x "$out/install.sh" "$out/umbra/umbra" "$out/umbractl/umbractl"
tar -C "$repo/dist" -czf "$repo/dist/$name.tar.gz" "$name"
sha256sum "$repo/dist/$name.tar.gz" | sed "s|$repo/dist/||" > "$repo/dist/$name.tar.gz.sha256"
echo "$repo/dist/$name.tar.gz ($(du -h "$repo/dist/$name.tar.gz" | cut -f1))"
