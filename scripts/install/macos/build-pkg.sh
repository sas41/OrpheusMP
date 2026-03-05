#!/usr/bin/env bash
# OrpheusMP — macOS .pkg builder
#
# Run this script on a macOS host (or in CI) after `dotnet publish`.
# It produces OrpheusMP.pkg using Apple's built-in pkgbuild + productbuild.
#
# Usage:
#   ./build-pkg.sh <publish-dir> <version> [output-dir]
#
#   publish-dir  Path to the dotnet publish output directory
#   version      Version string, e.g. "v2026-03-05-a1b2c3d"
#   output-dir   Where to write the .pkg (default: current directory)
#
# Requirements: macOS with Xcode Command Line Tools (pkgbuild, productbuild)
# Optional:     codesign + notarytool for a signed/notarised package

set -euo pipefail

# ── Args ──────────────────────────────────────────────────────────────────
PUBLISH_DIR="${1:?Usage: $0 <publish-dir> <version> [output-dir]}"
VERSION="${2:?Usage: $0 <publish-dir> <version> [output-dir]}"
OUTPUT_DIR="${3:-.}"

APP_NAME="Orpheus.Desktop"
APP_DISPLAY="OrpheusMP"
BUNDLE_ID="net.orpheusmp.desktop"
INSTALL_PREFIX="/Applications"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

# ── Build .app bundle layout under a payload root ─────────────────────────
# pkgbuild expects a "payload root" whose directory tree mirrors the
# target filesystem. Files placed at <root>/Applications/OrpheusMP.app/...
# are installed to /Applications/OrpheusMP.app/... on the target machine.

PAYLOAD_ROOT="${WORK_DIR}/payload"
BUNDLE="${PAYLOAD_ROOT}${INSTALL_PREFIX}/${APP_DISPLAY}.app"
MACOS_DIR="${BUNDLE}/Contents/MacOS"
RESOURCES_DIR="${BUNDLE}/Contents/Resources"

mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

echo "Copying published files into .app bundle ..."
cp -R "${PUBLISH_DIR}/." "$MACOS_DIR/"
chmod +x "${MACOS_DIR}/${APP_NAME}"

# ── Icon (PNG → ICNS) ────────────────────────────────────────────────────
for ICON_SRC in \
    "${PUBLISH_DIR}/assets/icon-256.png" \
    "${PUBLISH_DIR}/assets/icon.png"; do
  if [[ -f "$ICON_SRC" ]]; then
    ICONSET="${WORK_DIR}/AppIcon.iconset"
    mkdir -p "$ICONSET"
    for SIZE in 16 32 64 128 256 512; do
      sips -z $SIZE $SIZE "$ICON_SRC" \
        --out "${ICONSET}/icon_${SIZE}x${SIZE}.png"       &>/dev/null
      sips -z $((SIZE*2)) $((SIZE*2)) "$ICON_SRC" \
        --out "${ICONSET}/icon_${SIZE}x${SIZE}@2x.png"   &>/dev/null
    done
    iconutil -c icns "$ICONSET" -o "${RESOURCES_DIR}/AppIcon.icns"
    break
  fi
done

# ── Info.plist ────────────────────────────────────────────────────────────
# CFBundleDocumentTypes registers Orpheus as a handler for audio files.
# LSHandlerRank=Alternate means it appears in "Open With" without hijacking
# the system default; set to Default if you want it to be the default player.

cat > "${BUNDLE}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>              <string>${APP_DISPLAY}</string>
    <key>CFBundleDisplayName</key>       <string>${APP_DISPLAY}</string>
    <key>CFBundleIdentifier</key>        <string>${BUNDLE_ID}</string>
    <key>CFBundleVersion</key>           <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleExecutable</key>        <string>${APP_NAME}</string>
    <key>CFBundleIconFile</key>          <string>AppIcon</string>
    <key>CFBundlePackageType</key>       <string>APPL</string>
    <key>NSHighResolutionCapable</key>   <true/>
    <key>LSMinimumSystemVersion</key>    <string>12.0</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>        <string>MP3 Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>mp3</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>FLAC Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>flac</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>OGG Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>ogg</string><string>opus</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>AAC / M4A Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>m4a</string><string>aac</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>WAV Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>wav</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>WMA Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>wma</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>AIFF Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>aiff</string><string>aif</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>APE / WavPack Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>ape</string><string>wv</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>         <string>Matroska Audio</string>
            <key>CFBundleTypeRole</key>         <string>Viewer</string>
            <key>CFBundleTypeExtensions</key>   <array><string>mka</string></array>
            <key>LSHandlerRank</key>            <string>Alternate</string>
        </dict>
    </array>
</dict>
</plist>
PLIST

# ── postinstall script — registers the app with Launch Services ───────────
# pkgbuild runs this as root after the payload is installed.
SCRIPTS_DIR="${WORK_DIR}/scripts"
mkdir -p "$SCRIPTS_DIR"

cat > "${SCRIPTS_DIR}/postinstall" <<'POSTINSTALL'
#!/bin/bash
# Re-register OrpheusMP with Launch Services so the OS picks up
# the CFBundleDocumentTypes immediately without a reboot.
/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/\
LaunchServices.framework/Versions/A/Support/lsregister \
    -f "/Applications/OrpheusMP.app" 2>/dev/null || true
exit 0
POSTINSTALL
chmod +x "${SCRIPTS_DIR}/postinstall"

# ── Build component package ───────────────────────────────────────────────
COMPONENT_PKG="${WORK_DIR}/OrpheusMP-component.pkg"

echo "Running pkgbuild ..."
pkgbuild \
  --root        "$PAYLOAD_ROOT" \
  --identifier  "$BUNDLE_ID" \
  --version     "$VERSION" \
  --scripts     "$SCRIPTS_DIR" \
  --install-location "/" \
  "$COMPONENT_PKG"

# ── Distribution XML (productbuild) ──────────────────────────────────────
DIST_XML="${WORK_DIR}/distribution.xml"
cat > "$DIST_XML" <<DIST
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>OrpheusMP</title>
    <welcome    file="welcome.html"    mime-type="text/html" />
    <background file="background.png" mime-type="image/png"
                scaling="tofit" alignment="bottomleft" />
    <options customize="never" require-scripts="false" rootVolumeOnly="true" />
    <domains enable_localSystem="true" />
    <pkg-ref id="${BUNDLE_ID}" />
    <choices-outline>
        <line choice="default">
            <line choice="${BUNDLE_ID}" />
        </line>
    </choices-outline>
    <choice id="default" />
    <choice id="${BUNDLE_ID}" visible="false">
        <pkg-ref id="${BUNDLE_ID}" />
    </choice>
    <pkg-ref id="${BUNDLE_ID}" version="${VERSION}" onConclusion="none">OrpheusMP-component.pkg</pkg-ref>
</installer-gui-script>
DIST

# ── Optional welcome page ─────────────────────────────────────────────────
cat > "${WORK_DIR}/welcome.html" <<HTML
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"><style>
  body { font-family: -apple-system, sans-serif; padding: 20px; }
  h2   { color: #333; }
</style></head>
<body>
  <h2>Welcome to OrpheusMP</h2>
  <p>This will install <strong>OrpheusMP</strong> into <code>/Applications</code>.</p>
  <p>OrpheusMP will be registered as a handler for common audio file formats
     (MP3, FLAC, OGG, M4A, WAV, and more).</p>
</body>
</html>
HTML

# ── Copy background image if available ───────────────────────────────────
if [[ -f "${PUBLISH_DIR}/assets/icon-256.png" ]]; then
  cp "${PUBLISH_DIR}/assets/icon-256.png" "${WORK_DIR}/background.png"
fi

# ── productbuild ─────────────────────────────────────────────────────────
mkdir -p "$OUTPUT_DIR"
OUTPUT_PKG="${OUTPUT_DIR}/OrpheusMP-${VERSION}-macos.pkg"

echo "Running productbuild ..."
productbuild \
  --distribution  "$DIST_XML" \
  --package-path  "$WORK_DIR" \
  --resources     "$WORK_DIR" \
  "$OUTPUT_PKG"

echo ""
echo "Package written to: ${OUTPUT_PKG}"
