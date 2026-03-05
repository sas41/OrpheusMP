#!/usr/bin/env fish
#
# android-emulator.fish
# Installs (if needed) and starts the Orpheus Android emulator.
#
# Usage:
#   fish scripts/android-emulator.fish          # start emulator
#   fish scripts/android-emulator.fish --cold   # cold boot (ignore snapshot)

set ANDROID_HOME    /opt/android-sdk
set JAVA_HOME       /usr/lib/jvm/java-21-openjdk
set SDKMANAGER      $ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager
set AVDMANAGER      $ANDROID_HOME/cmdline-tools/latest/bin/avdmanager
set EMULATOR        $ANDROID_HOME/emulator/emulator
set ADB             $ANDROID_HOME/platform-tools/adb
set AVD_NAME        Orpheus
set API_LEVEL       36
set ABI             x86_64
set SYSTEM_IMAGE    "system-images;android-$API_LEVEL;google_apis;$ABI"
set DEVICE_PROFILE  "pixel_6"

# ── helpers ────────────────────────────────────────────────────────────────

function step
    echo ""
    echo "==> $argv"
end

function die
    echo "ERROR: $argv" >&2
    exit 1
end

function start_adb_if_needed
    if not test -x $ADB
        die "ADB not found at $ADB"
    end
    set adb_path (which adb 2>/dev/null)
    if test -z "$adb_path"
        set -gx PATH $ANDROID_HOME/platform-tools $PATH
    end
    if not adb get-state 1>/dev/null 2>&1
        echo "  Starting ADB server..."
        adb start-server
        if test $status -ne 0
            die "Failed to start ADB server."
        end
    else
        echo "  ADB server already running."
    end
end

# ── cold-boot flag ─────────────────────────────────────────────────────────

set COLD_BOOT ""
if contains -- --cold $argv
    set COLD_BOOT "-no-snapshot-load"
end

# ── 1. install emulator package ────────────────────────────────────────────

step "Checking Android Emulator..."
if not test -x $EMULATOR
    echo "  Emulator not found — installing..."
    echo "y" | JAVA_HOME=$JAVA_HOME $SDKMANAGER --sdk_root=$ANDROID_HOME "emulator"
    if test $status -ne 0
        die "Failed to install emulator."
    end
    # Re-check after install
    if not test -x $EMULATOR
        die "Emulator binary not found at $EMULATOR after install. Check sdkmanager output above."
    end
else
    echo "  Emulator already installed."
end

# ── 2. install system image ────────────────────────────────────────────────

step "Checking system image ($SYSTEM_IMAGE)..."
set installed (JAVA_HOME=$JAVA_HOME $SDKMANAGER --sdk_root=$ANDROID_HOME --list_installed 2>/dev/null)
if not string match -q "*$SYSTEM_IMAGE*" $installed
    echo "  System image not found — installing (this may take a few minutes)..."
    echo "y" | JAVA_HOME=$JAVA_HOME $SDKMANAGER --sdk_root=$ANDROID_HOME "$SYSTEM_IMAGE"
    if test $status -ne 0
        die "Failed to install system image."
    end
else
    echo "  System image already installed."
end

# ── 3. create AVD ──────────────────────────────────────────────────────────

step "Checking AVD '$AVD_NAME'..."
set avds (JAVA_HOME=$JAVA_HOME $AVDMANAGER list avd 2>/dev/null)
if not string match -q "*$AVD_NAME*" $avds
    echo "  AVD not found — creating..."
    echo "no" | JAVA_HOME=$JAVA_HOME $AVDMANAGER create avd \
        --name $AVD_NAME \
        --package $SYSTEM_IMAGE \
        --device $DEVICE_PROFILE \
        --force
    if test $status -ne 0
        die "Failed to create AVD."
    end
else
    echo "  AVD already exists."
end

# ── 4. start adb server ─────────────────────────────────────────────────────

step "Checking ADB server..."
start_adb_if_needed

# ── 5. start emulator ──────────────────────────────────────────────────────

step "Starting emulator '$AVD_NAME'..."
echo "  (close this terminal or press Ctrl+C to stop the emulator)"
echo ""

set EMULATOR_CMD $EMULATOR -avd $AVD_NAME -gpu auto -no-boot-anim
if test -n "$COLD_BOOT"
    set EMULATOR_CMD $EMULATOR_CMD $COLD_BOOT
end

JAVA_HOME=$JAVA_HOME ANDROID_HOME=$ANDROID_HOME $EMULATOR_CMD
