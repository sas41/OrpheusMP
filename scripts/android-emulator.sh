#!/usr/bin/env bash
#
# android-emulator.sh
# Installs (if needed) and starts the Orpheus Android emulator.
#
# Usage:
#   bash scripts/android-emulator.sh          # start emulator
#   bash scripts/android-emulator.sh --cold   # cold boot (ignore snapshot)

set -euo pipefail

ANDROID_HOME="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-/opt/android-sdk}}"
ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$ANDROID_HOME}"
JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-21-openjdk}"
SDKMANAGER="${SDKMANAGER:-$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager}"
AVDMANAGER="${AVDMANAGER:-$ANDROID_HOME/cmdline-tools/latest/bin/avdmanager}"
EMULATOR="${EMULATOR:-$ANDROID_HOME/emulator/emulator}"
ADB="${ADB:-$ANDROID_HOME/platform-tools/adb}"
AVD_NAME="${AVD_NAME:-Orpheus}"
API_LEVEL="${API_LEVEL:-36}"
ABI="${ABI:-x86_64}"
SYSTEM_IMAGE="${SYSTEM_IMAGE:-system-images;android-${API_LEVEL};google_apis;${ABI}}"
DEVICE_PROFILE="${DEVICE_PROFILE:-pixel_6}"

# ── helpers ────────────────────────────────────────────────────────────────

step() {
    echo ""
    echo "==> $*"
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

start_adb_if_needed() {
    if [ ! -x "$ADB" ]; then
        die "ADB not found at $ADB"
    fi
    if ! command -v adb &>/dev/null; then
        export PATH="$ANDROID_HOME/platform-tools:$PATH"
    fi
    if ! adb get-state &>/dev/null; then
        echo "  Starting ADB server..."
        adb start-server || die "Failed to start ADB server."
    else
        echo "  ADB server already running."
    fi
}

# ── flags ──────────────────────────────────────────────────────────────────

COLD_BOOT=""
ENSURE_MODE=false

for arg in "$@"; do
    case "$arg" in
        --cold)   COLD_BOOT="-no-snapshot-load" ;;
        --ensure) ENSURE_MODE=true ;;
    esac
done

# ── 1. install emulator package ────────────────────────────────────────────

step "Checking Android Emulator..."
if [ ! -x "$EMULATOR" ]; then
    echo "  Emulator not found — installing..."
    echo "y" | JAVA_HOME="$JAVA_HOME" "$SDKMANAGER" --sdk_root="$ANDROID_HOME" "emulator" \
        || die "Failed to install emulator."
    if [ ! -x "$EMULATOR" ]; then
        die "Emulator binary not found at $EMULATOR after install. Check sdkmanager output above."
    fi
else
    echo "  Emulator already installed."
fi

# ── 2. install system image ────────────────────────────────────────────────

step "Checking system image ($SYSTEM_IMAGE)..."
installed=$(JAVA_HOME="$JAVA_HOME" "$SDKMANAGER" --sdk_root="$ANDROID_HOME" --list_installed 2>/dev/null)
if [[ "$installed" != *"$SYSTEM_IMAGE"* ]]; then
    echo "  System image not found — installing (this may take a few minutes)..."
    echo "y" | JAVA_HOME="$JAVA_HOME" "$SDKMANAGER" --sdk_root="$ANDROID_HOME" "$SYSTEM_IMAGE" \
        || die "Failed to install system image."
else
    echo "  System image already installed."
fi

# ── 3. create AVD ──────────────────────────────────────────────────────────

step "Checking AVD '$AVD_NAME'..."
AVD_CONFIG="$HOME/.android/avd/${AVD_NAME}.avd/config.ini"
avds=$(JAVA_HOME="$JAVA_HOME" "$AVDMANAGER" list avd 2>/dev/null)
if [[ "$avds" != *"$AVD_NAME"* ]]; then
    echo "  AVD not found — creating..."
    echo "no" | JAVA_HOME="$JAVA_HOME" "$AVDMANAGER" create avd \
        --name "$AVD_NAME" \
        --package "$SYSTEM_IMAGE" \
        --device "$DEVICE_PROFILE" \
        --force \
        || die "Failed to create AVD."
else
    echo "  AVD already exists."
fi

# Ensure hardware keyboard is enabled (avdmanager creates AVDs with hw.keyboard=no).
if [ -f "$AVD_CONFIG" ]; then
    if grep -q "^hw.keyboard=no" "$AVD_CONFIG"; then
        sed -i 's/^hw.keyboard=no/hw.keyboard=yes/' "$AVD_CONFIG"
        echo "  Enabled hardware keyboard in AVD config."
    elif ! grep -q "^hw.keyboard=" "$AVD_CONFIG"; then
        echo "hw.keyboard=yes" >> "$AVD_CONFIG"
        echo "  Added hardware keyboard setting to AVD config."
    fi
fi

# ── helpers: emulator readiness ────────────────────────────────────────────

wait_for_emulator_boot() {
    echo "  Waiting for emulator to finish booting..."
    # First wait for ADB to see the device at all.
    adb -e wait-for-device 2>/dev/null || true
    # Then wait for the Android runtime to finish booting.
    local deadline=$(( $(date +%s) + 180 ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        local boot_state
        boot_state=$(adb -e shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
        if [ "$boot_state" = "1" ]; then
            echo "  Emulator is ready."
            return 0
        fi
        sleep 3
    done
    die "Emulator did not finish booting within 3 minutes."
}

emulator_is_running() {
    # Match any emulator entry regardless of state (device, offline, unauthorized)
    # so we don't launch a second instance during redeployment when ADB briefly
    # shows the emulator as offline.
    adb devices 2>/dev/null | grep -qE '^emulator-[0-9]+'
}

# ── 4. start adb server ─────────────────────────────────────────────────────

step "Checking ADB server..."
start_adb_if_needed

# ── 5. start emulator ──────────────────────────────────────────────────────

if $ENSURE_MODE; then
    step "Ensuring emulator '$AVD_NAME' is running..."
    # Wait for ADB to enumerate devices — it may have just started and needs
    # a moment to discover an already-running emulator.
    for i in 1 2 3; do
        sleep 2
        if emulator_is_running; then break; fi
    done
    if emulator_is_running; then
        echo "  Emulator already running."
        echo "EMULATOR_READY"
        exit 0
    fi
    EMULATOR_ARGS=(-avd "$AVD_NAME" -gpu auto -no-boot-anim -grpc-use-token)
    if [ -n "$COLD_BOOT" ]; then
        EMULATOR_ARGS+=("$COLD_BOOT")
    fi
    echo "  Launching emulator in background..."
    setsid env JAVA_HOME="$JAVA_HOME" ANDROID_HOME="$ANDROID_HOME" \
        "$EMULATOR" "${EMULATOR_ARGS[@]}" &>/dev/null &
    wait_for_emulator_boot
    echo "EMULATOR_READY"
    exit 0
fi

step "Starting emulator '$AVD_NAME'..."
echo "  (close this terminal or press Ctrl+C to stop the emulator)"
echo ""

EMULATOR_ARGS=(-avd "$AVD_NAME" -gpu auto -no-boot-anim -grpc-use-token)
if [ -n "$COLD_BOOT" ]; then
    EMULATOR_ARGS+=("$COLD_BOOT")
fi

exec env JAVA_HOME="$JAVA_HOME" ANDROID_HOME="$ANDROID_HOME" "$EMULATOR" "${EMULATOR_ARGS[@]}"
