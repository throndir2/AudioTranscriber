#!/bin/sh
set -eu
SYSTEM=0
YES=0
for arg in "$@"; do
  case "$arg" in
    --system) SYSTEM=1 ;;
    --yes|-y) YES=1 ;;
    *) echo "usage: ./install.sh [--system] [--yes]" >&2; exit 2 ;;
  esac
done
SRC_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ "$SYSTEM" -eq 1 ]; then
  PREFIX=/opt/audiotranscriber
  BIN_DIR=/usr/bin
  APP_DIR=/usr/share/applications
  ICON_ROOT=/usr/share/icons/hicolor
  META_DIR=/usr/share/metainfo
else
  PREFIX=${XDG_DATA_HOME:-$HOME/.local/share}/AudioTranscriber
  BIN_DIR=$HOME/.local/bin
  APP_DIR=${XDG_DATA_HOME:-$HOME/.local/share}/applications
  ICON_ROOT=${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor
  META_DIR=${XDG_DATA_HOME:-$HOME/.local/share}/metainfo
fi
as_root() { if [ "$(id -u)" -eq 0 ]; then "$@"; else sudo "$@"; fi; }
missing_prereqs() {
  missing=""
  for lib in libfontconfig.so.1 libX11.so.6 libICE.so.6 libSM.so.6 libXext.so.6 libXrandr.so.2 libXi.so.6 libXcursor.so.1 libpulse.so.0 libgomp.so.1; do
    if ! ldconfig -p 2>/dev/null | grep -q "$lib"; then missing="$missing $lib"; fi
  done
  if ! command -v pactl >/dev/null 2>&1; then missing="$missing pactl"; fi
  [ -z "$missing" ] || echo "$missing"
}
install_prereqs() {
  if command -v apt-get >/dev/null 2>&1; then as_root apt-get update; as_root apt-get install -y libc6 libstdc++6 libgcc-s1 libicu74 libfontconfig1 libx11-6 libice6 libsm6 libxext6 libxrandr2 libxi6 libxcursor1 libgomp1 libpulse0 pulseaudio-utils libvulkan1 pipewire-pulse
  elif command -v dnf >/dev/null 2>&1; then as_root dnf install -y glibc libstdc++ libgcc libicu fontconfig libX11 libICE libSM libXext libXrandr libXi libXcursor pulseaudio-libs pulseaudio-utils libgomp vulkan-loader pipewire-pulseaudio
  elif command -v zypper >/dev/null 2>&1; then as_root zypper --non-interactive install glibc libstdc++6 libgcc_s1 libicu fontconfig libX11-6 libICE6 libSM6 libXext6 libXrandr2 libXi6 libXcursor1 libpulse0 pulseaudio-utils libgomp1 libvulkan1 pipewire-pulseaudio
  elif command -v pacman >/dev/null 2>&1; then as_root pacman -Sy --needed --noconfirm glibc gcc-libs icu fontconfig libx11 libice libsm libxext libxrandr libxi libxcursor libpulse pulseaudio-utils libgomp vulkan-icd-loader pipewire-pulse
  else echo "No supported package manager found; install missing libraries manually." >&2; return 1; fi
}
missing=$(missing_prereqs || true)
if [ -n "$missing" ]; then
  echo "AudioTranscriber prerequisites appear to be missing:$missing"
  if [ "$YES" -eq 1 ]; then install_prereqs || true
  else printf 'Install prerequisites now? [y/N] '; read ans; case "$ans" in y|Y|yes|YES) install_prereqs || true ;; esac; fi
fi
as_root mkdir -p "$PREFIX" "$BIN_DIR" "$APP_DIR" "$META_DIR"
as_root cp -a "$SRC_DIR"/. "$PREFIX"/
as_root rm -f "$PREFIX/install.sh" "$PREFIX/uninstall.sh"
as_root chmod 755 "$PREFIX/AudioTranscriber.App" "$PREFIX/AudioTranscriber.Worker" "$PREFIX/ffmpeg/ffmpeg" "$PREFIX/ffmpeg/ffprobe" 2>/dev/null || true
as_root ln -sfn "$PREFIX/AudioTranscriber.App" "$BIN_DIR/audiotranscriber"
if [ -f "$SRC_DIR/audiotranscriber.desktop" ]; then as_root cp "$SRC_DIR/audiotranscriber.desktop" "$APP_DIR/audiotranscriber.desktop"; fi
if [ -f "$SRC_DIR/io.github.throndir2.AudioTranscriber.metainfo.xml" ]; then as_root cp "$SRC_DIR/io.github.throndir2.AudioTranscriber.metainfo.xml" "$META_DIR/io.github.throndir2.AudioTranscriber.metainfo.xml"; fi
if [ -d "$SRC_DIR/icons" ]; then
  for icon in "$SRC_DIR"/icons/audiotranscriber-*.png; do
    [ -f "$icon" ] || continue
    size=${icon##*-}; size=${size%.png}
    as_root mkdir -p "$ICON_ROOT/${size}x${size}/apps"
    as_root cp "$icon" "$ICON_ROOT/${size}x${size}/apps/audiotranscriber.png"
  done
elif [ -f "$SRC_DIR/AppIcon.png" ]; then
  as_root mkdir -p "$ICON_ROOT/256x256/apps"
  as_root cp "$SRC_DIR/AppIcon.png" "$ICON_ROOT/256x256/apps/audiotranscriber.png"
fi
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APP_DIR" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f "$ICON_ROOT" >/dev/null 2>&1 || true
echo "Installed AudioTranscriber to $PREFIX. Run: audiotranscriber"
