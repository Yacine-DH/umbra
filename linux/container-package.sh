#!/usr/bin/env bash
# Builds one Linux release tarball from a read-only source tree (inside the packaging container of scripts/linux-package.ps1,
# or directly on a Linux machine): copies the source to a clean folder without bin/obj/dist (a Windows checkout's build
# output must not leak in), strips the CRLF line endings a Windows checkout may have from the Linux files, runs
# linux/package.sh and copies the tarball and its .sha256 to the output folder.
#   linux/container-package.sh <source> <out> [x64|arm64]
set -eu
src=$1
out=$2
arch=${3:-x64}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
tar -C "$src" --exclude=./.git --exclude=./dist --exclude=./.claude --exclude=./site \
  --exclude=bin --exclude=obj --exclude=node_modules -cf - . | tar -C "$work" -xf -
find "$work/linux" "$work/Directory.Build.props" -type f \( -name '*.sh' -o -name '*.desktop' -o -name '*.props' \) \
  -exec sed -i 's/\r$//' {} +
bash "$work/linux/package.sh" "$arch"
ver=$(grep -oP '(?<=<Version>)[^<]+' "$work/Directory.Build.props")
name="umbra-$ver-linux-$arch.tar.gz"
(cd "$work/dist" && sha256sum -c "$name.sha256")
mkdir -p "$out"
cp "$work/dist/$name" "$work/dist/$name.sha256" "$out/"
echo "$out/$name"
