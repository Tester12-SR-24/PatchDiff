#!/bin/sh
# PatchDiff installer for Linux.
#
# Installs the app into a folder you choose, adds a `patchdiff` launcher to your
# PATH, and registers a desktop entry so it shows up in the application menu.
#
#   ./install.sh                          # default location
#   ./install.sh --dir /opt/patchdiff     # choose the folder
#   ./install.sh --uninstall              # remove it again
#
# Defaults: /opt/patchdiff when run as root, otherwise ~/.local/share/patchdiff.
# The launcher goes to /usr/local/bin as root, otherwise ~/.local/bin.
#
# Run it from the extracted release archive (the folder holding PatchDiff and
# patchdiff-cli).

set -eu

APP_NAME="PatchDiff"
APP_ID="patchdiff"
SRC_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

if [ "$(id -u)" -eq 0 ]; then
    DEFAULT_DIR="/opt/$APP_ID"
    DEFAULT_BIN="/usr/local/bin"
    DEFAULT_APPS="/usr/share/applications"
    DEFAULT_ICONS="/usr/share/icons/hicolor"
else
    DEFAULT_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/$APP_ID"
    DEFAULT_BIN="$HOME/.local/bin"
    DEFAULT_APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
    DEFAULT_ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
fi

INSTALL_DIR="$DEFAULT_DIR"
BIN_DIR="$DEFAULT_BIN"
APPS_DIR="$DEFAULT_APPS"
ICONS_DIR="$DEFAULT_ICONS"
UNINSTALL=0
ASSUME_YES=0

usage() {
    cat <<EOF
PatchDiff installer

Usage: ./install.sh [options]

  --dir DIR        install folder (default: $DEFAULT_DIR)
  --bin-dir DIR    where to put the 'patchdiff' launcher (default: $DEFAULT_BIN)
  --uninstall      remove a previous install
  --yes            do not ask for confirmation
  -h, --help       show this help
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        --dir=*) INSTALL_DIR="${1#*=}"; shift ;;
        --bin-dir) BIN_DIR="$2"; shift 2 ;;
        --bin-dir=*) BIN_DIR="${1#*=}"; shift ;;
        --uninstall) UNINSTALL=1; shift ;;
        --yes|-y) ASSUME_YES=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# Expand a leading ~ so --dir ~/apps works even when quoted.
case "$INSTALL_DIR" in
    "~"|"~/"*) INSTALL_DIR="$HOME${INSTALL_DIR#\~}" ;;
esac
case "$BIN_DIR" in
    "~"|"~/"*) BIN_DIR="$HOME${BIN_DIR#\~}" ;;
esac

LAUNCHER="$BIN_DIR/$APP_ID"
DESKTOP_FILE="$APPS_DIR/$APP_ID.desktop"

confirm() {
    [ "$ASSUME_YES" -eq 1 ] && return 0
    printf '%s [y/N] ' "$1"
    read -r answer || answer=""
    case "$answer" in
        y|Y|yes|YES) return 0 ;;
        *) return 1 ;;
    esac
}

if [ "$UNINSTALL" -eq 1 ]; then
    # Reuse the locations recorded at install time so a custom --bin-dir is cleaned up too.
    META="$INSTALL_DIR/.patchdiff-install"
    if [ -f "$META" ]; then
        recorded_bin=$(sed -n 's/^BIN_DIR=//p' "$META")
        recorded_apps=$(sed -n 's/^APPS_DIR=//p' "$META")
        recorded_icons=$(sed -n 's/^ICONS_DIR=//p' "$META")
        [ -n "$recorded_bin" ] && BIN_DIR="$recorded_bin"
        [ -n "$recorded_apps" ] && APPS_DIR="$recorded_apps"
        [ -n "$recorded_icons" ] && ICONS_DIR="$recorded_icons"
        LAUNCHER="$BIN_DIR/$APP_ID"
        DESKTOP_FILE="$APPS_DIR/$APP_ID.desktop"
    fi
    echo "Removing PatchDiff from $INSTALL_DIR"
    rm -f "$LAUNCHER" "$DESKTOP_FILE"
    for size in 128 256 512; do
        rm -f "$ICONS_DIR/${size}x${size}/apps/$APP_ID.png"
    done
    rm -rf "$INSTALL_DIR"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS_DIR" 2>/dev/null || true
    echo "Done. Your data in ~/.local/share/PatchDiff was left untouched."
    exit 0
fi

if [ ! -f "$SRC_DIR/$APP_NAME" ]; then
    echo "error: $APP_NAME not found next to this script ($SRC_DIR)." >&2
    echo "       Run install.sh from the extracted release archive." >&2
    exit 1
fi

echo "PatchDiff installer"
echo "  from:     $SRC_DIR"
echo "  install:  $INSTALL_DIR"
echo "  launcher: $LAUNCHER"
echo

if ! confirm "Install PatchDiff to $INSTALL_DIR?"; then
    echo "Cancelled."
    exit 1
fi

mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$APPS_DIR"

# Copy the app, keeping executable bits. Skip the installer itself and any
# AppImage sitting in the same folder.
for entry in "$SRC_DIR"/*; do
    base=$(basename "$entry")
    case "$base" in
        install.sh|*.AppImage) continue ;;
    esac
    # Remove first: replacing a running binary in place fails with ETXTBSY
    # ("Text file busy") on Linux, and unlinking it always works.
    rm -rf "$INSTALL_DIR/$base"
    cp -a "$entry" "$INSTALL_DIR/"
done
chmod -R a+rX "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/$APP_NAME"
[ -f "$INSTALL_DIR/patchdiff-cli" ] && chmod +x "$INSTALL_DIR/patchdiff-cli"

# Launcher on PATH.
cat > "$LAUNCHER" <<EOF
#!/bin/sh
exec "$INSTALL_DIR/$APP_NAME" "\$@"
EOF
chmod 755 "$LAUNCHER"

# Desktop entry + icon.
if [ -f "$SRC_DIR/patchdiff.png" ]; then
    for size in 128 256 512; do
        mkdir -p "$ICONS_DIR/${size}x${size}/apps"
        cp "$SRC_DIR/patchdiff.png" "$ICONS_DIR/${size}x${size}/apps/$APP_ID.png"
    done
fi

cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=PatchDiff
Comment=Watch game DLLs and see what a patch changed
Exec=$LAUNCHER
Icon=$APP_ID
Terminal=false
Categories=Development;
EOF
chmod 644 "$DESKTOP_FILE"

# Record where everything went so --uninstall can clean up custom locations.
cat > "$INSTALL_DIR/.patchdiff-install" <<EOF
BIN_DIR=$BIN_DIR
APPS_DIR=$APPS_DIR
ICONS_DIR=$ICONS_DIR
EOF

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS_DIR" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f "$ICONS_DIR" 2>/dev/null || true

echo
echo "Installed."
echo "  Run:     $LAUNCHER"
echo "  Or find: PatchDiff in your application menu"
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo "  Note:    $BIN_DIR is not on your PATH — add it to use 'patchdiff' directly." ;;
esac
echo "  Remove:  $SRC_DIR/install.sh --uninstall --dir \"$INSTALL_DIR\""
