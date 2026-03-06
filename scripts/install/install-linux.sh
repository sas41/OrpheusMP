#!/usr/bin/env bash
# OrpheusMP — Linux installer
#
# Usage:
#   ./install-linux.sh           # installs for current user (~/.local)
#   sudo ./install-linux.sh      # installs system-wide (/usr/local)
#
# What this script does:
#   1. Copies OrpheusMP binaries to the appropriate prefix
#   2. Copies the app icon
#   3. Writes a .desktop file and registers it with the system
#   4. Registers audio MIME-type associations via xdg-mime / update-desktop-database
#   5. Optionally makes Orpheus the default handler for supported audio formats
#
# Requirements: bash 4+, xdg-utils (usually pre-installed), libvlc (vlc)
#
# To uninstall, run: ./install-linux.sh --uninstall

set -euo pipefail

# ── Determine install prefix (root vs. user) ──────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
  PREFIX="/usr/local"
  DESKTOP_DIR="/usr/share/applications"
  ICON_DIR="/usr/share/icons/hicolor"
  MIME_DIR="/usr/share/mime"
else
  PREFIX="${HOME}/.local"
  DESKTOP_DIR="${HOME}/.local/share/applications"
  ICON_DIR="${HOME}/.local/share/icons/hicolor"
  MIME_DIR="${HOME}/.local/share/mime"
fi

BIN_DIR="${PREFIX}/bin"
LIB_DIR="${PREFIX}/lib/orpheusmp"
APP_NAME="orpheusmp"
APP_DISPLAY="OrpheusMP"
EXEC_NAME="Orpheus.Desktop"

# ── Locate files relative to this script ─────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${SCRIPT_DIR}/app"

# ── Uninstall mode ────────────────────────────────────────────────────────
if [[ "${1:-}" == "--uninstall" ]]; then
  echo "Uninstalling ${APP_DISPLAY} ..."
  rm -f  "${BIN_DIR}/${APP_NAME}"
  rm -rf "${LIB_DIR}"
  rm -f  "${DESKTOP_DIR}/${APP_NAME}.desktop"
  rm -f  "${ICON_DIR}/256x256/apps/${APP_NAME}.png"
  rm -f  "${ICON_DIR}/scalable/apps/${APP_NAME}.svg"
  # Remove MIME-type association
  if command -v xdg-mime &>/dev/null; then
    xdg-mime uninstall "${MIME_DIR}/packages/${APP_NAME}-audio.xml" 2>/dev/null || true
  fi
  rm -f "${MIME_DIR}/packages/${APP_NAME}-audio.xml"
  command -v update-desktop-database &>/dev/null && \
    update-desktop-database "${DESKTOP_DIR}" 2>/dev/null || true
  command -v update-mime-database &>/dev/null && \
    update-mime-database "${MIME_DIR}" 2>/dev/null || true
  echo "Uninstallation complete."
  exit 0
fi

# ── Check for libvlc ──────────────────────────────────────────────────────
if ! ldconfig -p 2>/dev/null | grep -q libvlc || ! command -v vlc &>/dev/null; then
  echo "WARNING: libvlc does not appear to be installed."
  echo "Install it first, e.g.:"
  echo "  Ubuntu/Debian: sudo apt install vlc libvlc-dev"
  echo "  Fedora:        sudo dnf install vlc-libs"
  echo "  Arch:          sudo pacman -S vlc"
  echo ""
fi

# ── Install binaries ──────────────────────────────────────────────────────
echo "Installing ${APP_DISPLAY} to ${LIB_DIR} ..."
mkdir -p "$LIB_DIR" "$BIN_DIR"

rsync -a --delete "${APP_DIR}/" "${LIB_DIR}/" 2>/dev/null || \
  (rm -rf "${LIB_DIR}" && mkdir -p "${LIB_DIR}" && \
   cd "$APP_DIR" && find . -print0 | cpio -0pdm "$LIB_DIR")

chmod +x "${LIB_DIR}/${EXEC_NAME}"

# ── Wrapper script in PATH ───────────────────────────────────────────────
cat > "${BIN_DIR}/${APP_NAME}" <<WRAPPER
#!/usr/bin/env bash
exec "${LIB_DIR}/${EXEC_NAME}" "\$@"
WRAPPER
chmod +x "${BIN_DIR}/${APP_NAME}"
echo "Launcher written to ${BIN_DIR}/${APP_NAME}"

# ── Install icon ──────────────────────────────────────────────────────────
ICON_PNG="${APP_DIR}/assets/icon-256.png"
ICON_SVG="${APP_DIR}/assets/icon.svg"

if [[ -f "$ICON_PNG" ]]; then
  mkdir -p "${ICON_DIR}/256x256/apps"
  cp "$ICON_PNG" "${ICON_DIR}/256x256/apps/${APP_NAME}.png"
fi

if [[ -f "$ICON_SVG" ]]; then
  mkdir -p "${ICON_DIR}/scalable/apps"
  cp "$ICON_SVG" "${ICON_DIR}/scalable/apps/${APP_NAME}.svg"
fi

# ── Write .desktop file ───────────────────────────────────────────────────
AUDIO_MIME_TYPES="audio/mpeg;audio/flac;audio/ogg;audio/opus;audio/mp4;audio/aac;\
audio/x-wav;audio/x-ms-wma;audio/x-aiff;audio/x-ape;audio/x-wavpack;\
audio/x-matroska;"

mkdir -p "$DESKTOP_DIR"
cat > "${DESKTOP_DIR}/${APP_NAME}.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=${APP_DISPLAY}
GenericName=Music Player
Comment=Open-source music player powered by LibVLC
Exec=${BIN_DIR}/${APP_NAME} %U
Icon=${APP_NAME}
Terminal=false
Categories=Audio;Music;Player;AudioVideo;
MimeType=${AUDIO_MIME_TYPES}
Keywords=music;audio;player;mp3;flac;
StartupWMClass=Orpheus.Desktop
DESKTOP

echo ".desktop file written to ${DESKTOP_DIR}/${APP_NAME}.desktop"

# ── Register MIME types ───────────────────────────────────────────────────
mkdir -p "${MIME_DIR}/packages"
cat > "${MIME_DIR}/packages/${APP_NAME}-audio.xml" <<MIMEXML
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="audio/mpeg">
    <glob pattern="*.mp3"/>
  </mime-type>
  <mime-type type="audio/flac">
    <glob pattern="*.flac"/>
  </mime-type>
  <mime-type type="audio/ogg">
    <glob pattern="*.ogg"/>
  </mime-type>
  <mime-type type="audio/opus">
    <glob pattern="*.opus"/>
  </mime-type>
  <mime-type type="audio/mp4">
    <glob pattern="*.m4a"/>
    <glob pattern="*.aac"/>
  </mime-type>
  <mime-type type="audio/x-wav">
    <glob pattern="*.wav"/>
  </mime-type>
  <mime-type type="audio/x-ms-wma">
    <glob pattern="*.wma"/>
  </mime-type>
  <mime-type type="audio/x-aiff">
    <glob pattern="*.aiff"/>
    <glob pattern="*.aif"/>
  </mime-type>
  <mime-type type="audio/x-ape">
    <glob pattern="*.ape"/>
  </mime-type>
  <mime-type type="audio/x-wavpack">
    <glob pattern="*.wv"/>
  </mime-type>
  <mime-type type="audio/x-matroska">
    <glob pattern="*.mka"/>
  </mime-type>
</mime-info>
MIMEXML

# ── Update system databases ───────────────────────────────────────────────
if command -v update-mime-database &>/dev/null; then
  update-mime-database "${MIME_DIR}" 2>/dev/null || true
  echo "MIME database updated."
fi

if command -v update-desktop-database &>/dev/null; then
  update-desktop-database "${DESKTOP_DIR}" 2>/dev/null || true
  echo "Desktop database updated."
fi

if command -v gtk-update-icon-cache &>/dev/null; then
  gtk-update-icon-cache -q -t -f "${ICON_DIR}" 2>/dev/null || true
fi

# ── Optionally set as default handler ────────────────────────────────────
echo ""
CURRENT_DEFAULT=$(xdg-mime query default audio/mpeg 2>/dev/null || true)
if [[ "$CURRENT_DEFAULT" == "${APP_NAME}.desktop" ]]; then
  echo "OrpheusMP is already the default audio player."
  SET_DEFAULT="n"
else
  read -r -p "Set OrpheusMP as the default player for all supported audio files? [y/N] " SET_DEFAULT
fi
if [[ "$SET_DEFAULT" =~ ^[Yy]$ ]]; then
  AUDIO_MIMES=(
    audio/mpeg audio/flac audio/ogg audio/opus audio/mp4 audio/aac
    audio/x-wav audio/x-ms-wma audio/x-aiff audio/x-ape
    audio/x-wavpack audio/x-matroska
  )
  for mime in "${AUDIO_MIMES[@]}"; do
    xdg-mime default "${APP_NAME}.desktop" "$mime" 2>/dev/null || true
  done
  echo "OrpheusMP set as default audio player."
fi

echo ""
echo "Installation complete!"
echo "Run '${APP_NAME}' from a terminal or find it in your application launcher."
