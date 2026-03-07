#!/usr/bin/env bash
#
# android-seed-media.sh
# Copies media files from the repo into the running Android emulator.
#
# Usage:
#   bash scripts/android-seed-media.sh
#   bash scripts/android-seed-media.sh dev-media
#   bash scripts/android-seed-media.sh dev-media /sdcard/Music/Test

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

ANDROID_HOME="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-/opt/android-sdk}}"
ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$ANDROID_HOME}"
ADB="${ADB:-$ANDROID_HOME/platform-tools/adb}"

SOURCE_DIR="${1:-dev-media}"
TARGET_DIR="${2:-/sdcard/Music}"

if [ "$SOURCE_DIR" = "--help" ] || [ "$SOURCE_DIR" = "-h" ]; then
    cat <<'EOF'
Usage:
  bash scripts/android-seed-media.sh [source-dir] [target-dir]

Defaults:
  source-dir  dev-media
  target-dir  /sdcard/Music

Examples:
  bash scripts/android-seed-media.sh
  bash scripts/android-seed-media.sh test-media
  bash scripts/android-seed-media.sh test-media /sdcard/Music/Test
EOF
    exit 0
fi

if [[ "$SOURCE_DIR" != /* ]]; then
    SOURCE_DIR="$REPO_ROOT/$SOURCE_DIR"
fi

step() {
    echo ""
    echo "==> $*"
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

ensure_adb() {
    if ! command -v adb &>/dev/null; then
        if [ ! -x "$ADB" ]; then
            die "ADB not found in PATH and not found at $ADB"
        fi
        export PATH="$ANDROID_HOME/platform-tools:$PATH"
    fi

    adb start-server >/dev/null 2>&1 || die "Failed to start ADB server."
}

ensure_source_dir() {
    [ -d "$SOURCE_DIR" ] || die "Source directory not found: $SOURCE_DIR"

    if ! find "$SOURCE_DIR" -type f -print -quit | grep -q .; then
        die "Source directory has no files: $SOURCE_DIR"
    fi
}

ensure_emulator() {
    if ! adb -e get-state >/dev/null 2>&1; then
        die "No running emulator detected. Start it first with: bash scripts/android-emulator.sh"
    fi
}

step "Checking source media..."
ensure_source_dir
echo "  Source: $SOURCE_DIR"
echo "  Target: $TARGET_DIR"

step "Checking ADB..."
ensure_adb

step "Checking emulator..."
ensure_emulator
echo "  Emulator is available."

step "Creating target folder..."
adb -e shell mkdir -p "$TARGET_DIR" >/dev/null

step "Copying media files..."
adb -e push "$SOURCE_DIR/." "$TARGET_DIR/"

step "Done"
echo "  Seeded media into $TARGET_DIR"
