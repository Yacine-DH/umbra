#!/usr/bin/env bash
# Builds the self-contained Linux tarball: dist/umbra-<version>-linux-<arch>.tar.gz with the app, umbractl, the app menu
# entry, the icons and install.sh. .NET is bundled; GTK 4 and WebKitGTK 6 come from the system.
#   linux/package.sh [x64|arm64]      (default x64). Needs the .NET 10 SDK, gcc and libwebkitgtk-6.0-dev.
set -eu
repo=$(cd "$(dirname "$0")/.." && pwd)
arch=${1:-x64}
ver=$(grep -oP '(?<=<Version>)[^<]+' "$repo/Directory.Build.props")
name="umbra-$ver-linux-$arch"
out="$repo/dist/$name"
rm -rf "$out"
dotnet publish "$repo/src/Umbra.Linux" -c Release -r "linux-$arch" --self-contained true -p:DebugType=none -o "$out/umbra" --nologo -v q
dotnet publish "$repo/src/Umbra.Cli" -c Release -r "linux-$arch" --self-contained true -p:DebugType=none -o "$out/umbractl" --nologo -v q
[ -f "$out/umbra/webext/libumbra-webext.so" ] || { echo "the sandbox extension is missing from the build" >&2; exit 1; }
echo "$ver" > "$out/umbra/VERSION"
cp "$repo/linux/install.sh" "$repo/linux/io.github.aminechr54.Umbra.desktop" "$out/"
cp "$repo/LICENSE" "$out/" 2>/dev/null || true
chmod +x "$out/install.sh" "$out/umbra/umbra" "$out/umbractl/umbractl"
tar -C "$repo/dist" -czf "$repo/dist/$name.tar.gz" "$name"
sha256sum "$repo/dist/$name.tar.gz" | sed "s|$repo/dist/||" > "$repo/dist/$name.tar.gz.sha256"
echo "$repo/dist/$name.tar.gz ($(du -h "$repo/dist/$name.tar.gz" | cut -f1))"
