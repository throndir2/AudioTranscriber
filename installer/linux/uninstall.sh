#!/bin/sh
set -eu
SYSTEM=0
for arg in "$@"; do case "$arg" in --system) SYSTEM=1 ;; *) echo "usage: ./uninstall.sh [--system]" >&2; exit 2 ;; esac; done
if [ "$SYSTEM" -eq 1 ]; then PREFIX=/opt/audiotranscriber; BIN_DIR=/usr/bin; APP_DIR=/usr/share/applications; ICON_ROOT=/usr/share/icons/hicolor; META_DIR=/usr/share/metainfo; else PREFIX=${XDG_DATA_HOME:-$HOME/.local/share}/AudioTranscriber; BIN_DIR=$HOME/.local/bin; APP_DIR=${XDG_DATA_HOME:-$HOME/.local/share}/applications; ICON_ROOT=${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor; META_DIR=${XDG_DATA_HOME:-$HOME/.local/share}/metainfo; fi
as_root() { if [ "$(id -u)" -eq 0 ]; then "$@"; else sudo "$@"; fi; }
as_root rm -f "$BIN_DIR/audiotranscriber" "$APP_DIR/audiotranscriber.desktop" "$META_DIR/io.github.throndir2.AudioTranscriber.metainfo.xml"
for size in 16 32 48 64 128 256 512; do as_root rm -f "$ICON_ROOT/${size}x${size}/apps/audiotranscriber.png"; done
as_root rm -rf "$PREFIX"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APP_DIR" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f "$ICON_ROOT" >/dev/null 2>&1 || true
echo "Removed AudioTranscriber application files. User data was left in place."
