#!/usr/bin/env bash

set -euo pipefail

cd /workspace

PROJECT="src/Orpheus.Android/Orpheus.Android.csproj"
CONFIGURATION="${CONFIGURATION:-Release}"
FRAMEWORK="${FRAMEWORK:-net10.0-android}"
ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
PACKAGE_NAME="${PACKAGE_NAME:-net.orpheusmp.android}"

find_apk() {
    find src/Orpheus.Android -path "*/publish/*.apk" | head -1
}

publish_apk() {
    dotnet publish "$PROJECT" \
      --configuration "$CONFIGURATION" \
      --framework "$FRAMEWORK" \
      -p:AndroidSdkDirectory="$ANDROID_HOME" \
      -p:AndroidPackageFormat=apk \
      -p:AndroidKeyStore=false
}

install_apk() {
    local apk
    apk=$(find_apk)
    if [ -z "$apk" ]; then
        echo "APK not found, publishing first..."
        publish_apk
        apk=$(find_apk)
    fi

    if [ -z "$apk" ]; then
        echo "ERROR: APK still not found after publish." >&2
        exit 1
    fi

    adb -e install -r "$apk"
}

run_app() {
    adb -e shell monkey -p "$PACKAGE_NAME" -c android.intent.category.LAUNCHER 1
}

case "${1:-help}" in
    help)
        cat <<'EOF'
Commands:
  publish          Build APK
  install          Install APK to running emulator/device
  run              Launch installed app
  deploy           Publish, install, and launch
  emulator         Run scripts/android-emulator.sh --ensure
  shell            Open interactive shell in container
EOF
        ;;
    publish)
        publish_apk
        ;;
    install)
        install_apk
        ;;
    run)
        run_app
        ;;
    deploy)
        publish_apk
        install_apk
        run_app
        ;;
    emulator)
        bash scripts/android-emulator.sh --ensure
        ;;
    shell)
        exec bash
        ;;
    *)
        echo "Unknown command: $1" >&2
        exit 1
        ;;
esac
