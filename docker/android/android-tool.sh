#!/usr/bin/env bash

set -euo pipefail

cd /workspace

PROJECT="src/Orpheus.Android/Orpheus.Android.csproj"
CONFIGURATION="${CONFIGURATION:-Debug}"
FRAMEWORK="${FRAMEWORK:-net10.0-android}"
ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
PACKAGE_NAME="${PACKAGE_NAME:-net.orpheusmp.android}"
UNINSTALL_ON_SIGNATURE_MISMATCH="${UNINSTALL_ON_SIGNATURE_MISMATCH:-true}"
CONTAINER_ARTIFACTS_ROOT="${CONTAINER_ARTIFACTS_ROOT:-/tmp/orpheus-android-build}"

mkdir -p "$CONTAINER_ARTIFACTS_ROOT/nuget" "$CONTAINER_ARTIFACTS_ROOT/msbuild"

DOTNET_BUILD_ARGS=(
    -p:MSBuildProjectExtensionsPath="$CONTAINER_ARTIFACTS_ROOT/msbuild/"
)

export NUGET_PACKAGES="$CONTAINER_ARTIFACTS_ROOT/nuget"

find_apk() {
    local signed_apk
    local unsigned_apk

    signed_apk=$(find src/Orpheus.Android -path "*/publish/*-Signed.apk" | sort | head -1)
    if [ -n "$signed_apk" ]; then
        printf '%s\n' "$signed_apk"
        return 0
    fi

    unsigned_apk=$(find src/Orpheus.Android -path "*/publish/*.apk" ! -name "*-Signed.apk" | sort | head -1)
    if [ -n "$unsigned_apk" ]; then
        printf '%s\n' "$unsigned_apk"
    fi
}

publish_apk() {
    dotnet restore "$PROJECT" \
      -p:TargetFramework="$FRAMEWORK" \
      "${DOTNET_BUILD_ARGS[@]}"

    dotnet clean "$PROJECT" \
      --configuration "$CONFIGURATION" \
      --framework "$FRAMEWORK" \
      "${DOTNET_BUILD_ARGS[@]}"

    dotnet publish "$PROJECT" \
      --configuration "$CONFIGURATION" \
      --framework "$FRAMEWORK" \
      --no-restore \
      "${DOTNET_BUILD_ARGS[@]}" \
      -p:AndroidSdkDirectory="$ANDROID_HOME" \
      -p:AndroidPackageFormat=apk \
      -p:AndroidKeyStore=false
}

install_apk() {
    local apk
    local output
    local status

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

    set +e
    output=$(adb -e install -r "$apk" 2>&1)
    status=$?
    set -e

    if [ $status -eq 0 ]; then
        printf '%s\n' "$output"
        return 0
    fi

    printf '%s\n' "$output" >&2

    if [[ "$output" == *"INSTALL_FAILED_UPDATE_INCOMPATIBLE"* ]] && [ "$UNINSTALL_ON_SIGNATURE_MISMATCH" = "true" ]; then
        echo "Signature mismatch detected for $PACKAGE_NAME; uninstalling existing app and retrying..."
        adb -e uninstall "$PACKAGE_NAME"
        adb -e install -r "$apk"
        return 0
    fi

    return $status
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
