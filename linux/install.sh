#!/usr/bin/env bash
# Installs Umbra for the current user from the unpacked tarball (no root): the app in ~/.local/opt/umbra, `umbra` and
# `umbractl` in ~/.local/bin, the app menu entry and the icon. Running it again updates in place; your widgets and settings
# (~/.local/share/umbra) are never touched.   ./install.sh            install or update
#                                              ./install.sh --uninstall  remove the app (keeps your data)
set -eu
here=$(cd "$(dirname "$0")" && pwd)
prefix=${UMBRA_PREFIX:-$HOME/.local}
app="$prefix/opt/umbra"
bin="$prefix/bin"
share=${XDG_DATA_HOME:-$HOME/.local/share}
id=io.github.aminechr54.Umbra

if [ "${1:-}" = "--uninstall" ]; then
  pkill -u "$(id -u)" -x umbra 2>/dev/null || true
  rm -rf "$app"
  rm -f "$bin/umbra" "$bin/umbractl" "$share/applications/$id.desktop"
  find "$share/icons/hicolor" -name "$id*.png" -delete 2>/dev/null || true
  rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/$id.desktop"
  echo "Umbra removed. Your widgets and settings are still in $share/umbra (delete that folder to remove them too)."
  exit 0
fi

# Runtime libraries (.NET is bundled; GTK 4 and WebKitGTK 6 come from the system).
missing=""
for lib in libgtk-4.so.1 libwebkitgtk-6.0.so.4; do
  ldconfig -p 2>/dev/null | grep -q "$lib" || missing="$missing $lib"
done
if [ -n "$missing" ]; then
  echo "Missing system libraries:$missing"
  echo "  Ubuntu/Debian: sudo apt install libgtk-4-1 libwebkitgtk-6.0-4"
  echo "  Fedora:        sudo dnf install gtk4 webkitgtk6.0"
  echo "  Arch:          sudo pacman -S gtk4 webkitgtk-6.0"
  exit 1
fi
# Optional: native Wayland pinning on KDE Plasma, Sway, Hyprland (#186). Without it Umbra pins through XWayland.
if ! ldconfig -p 2>/dev/null | grep -q libgtk4-layer-shell.so.0; then
  echo "Optional: install gtk4-layer-shell for native Wayland pinning on KDE Plasma (Ubuntu: libgtk4-layer-shell0, Fedora/Arch: gtk4-layer-shell)."
fi

running=0
if pgrep -u "$(id -u)" -x umbra >/dev/null 2>&1; then
  running=1
  "$app/umbractl/umbractl" quit >/dev/null 2>&1 || pkill -u "$(id -u)" -x umbra || true
  sleep 1
fi

mkdir -p "$prefix/opt" "$bin" "$share/applications"
rm -rf "$app.new"
mkdir -p "$app.new"
cp -r "$here/umbra" "$here/umbractl" "$app.new/"
rm -rf "$app"
mv "$app.new" "$app"
ln -sf "$app/umbra/umbra" "$bin/umbra"
ln -sf "$app/umbractl/umbractl" "$bin/umbractl"
sed "s|^Exec=umbra$|Exec=$app/umbra/umbra|" "$here/$id.desktop" > "$share/applications/$id.desktop"
cp -r "$app/umbra/icons/hicolor" "$share/icons/" 2>/dev/null || { mkdir -p "$share/icons"; cp -r "$app/umbra/icons/hicolor" "$share/icons/"; }
rm -f "$share/icons/hicolor/index.theme" # the system theme's index wins; ours only lists our own folders
command -v update-desktop-database >/dev/null && update-desktop-database -q "$share/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$share/icons/hicolor" 2>/dev/null || true

echo "Umbra $(cat "$app/umbra/VERSION" 2>/dev/null) installed in $app"
case ":$PATH:" in *":$bin:"*) ;; *) echo "Note: $bin is not on your PATH; open Umbra from the app menu, or add it to PATH for umbractl." ;; esac
if [ $running = 1 ]; then nohup "$app/umbra/umbra" --background >/dev/null 2>&1 & echo "Restarted Umbra."; else echo "Open it from the app menu (Umbra) or run: umbra"; fi
