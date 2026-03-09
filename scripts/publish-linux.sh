#!/usr/bin/env bash
# publish-linux.sh — local Linux build that mirrors the GitHub Actions workflow.
#
# Usage:
#   ./scripts/publish-linux.sh [VERSION]
#
# VERSION defaults to a date+git-sha string identical to what CI generates:
#   v<YYYY-MM-DD>-<short-sha>
#
# Output:
#   OrpheusMP-<VERSION>-linux-x64.zip     (in the repo root, when zip is available)
#   OrpheusMP-<VERSION>-linux-x64.tar.gz  (fallback when zip is not installed)
#
# The zip layout is:
#   install-linux.sh          (installer script)
#   app/                      (self-contained publish output)
#     Orpheus.Desktop
#     *.so / *.dll / ...
#     assets/
#       icon-256.png
#       icon.svg

set -euo pipefail

# ── Resolve repo root ────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# ── Version ──────────────────────────────────────────────────────────────────
if [[ $# -ge 1 && -n "$1" ]]; then
    VERSION="$1"
else
    DATE=$(date -u +%Y-%m-%d)
    SHORT_SHA=$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo "unknown")
    VERSION="v${DATE}-${SHORT_SHA}"
fi

ARTIFACT_NAME="OrpheusMP-${VERSION}-linux-x64"

echo "==> Building ${ARTIFACT_NAME}"
echo

# ── Paths ────────────────────────────────────────────────────────────────────
PUBLISH_DIR="$REPO_ROOT/publish/linux"
STAGE_DIR="$REPO_ROOT/publish/linux-zip"
PROJECT="$REPO_ROOT/src/Orpheus.Desktop/Orpheus.Desktop.csproj"

# Clean previous artifacts
rm -rf "$PUBLISH_DIR" "$STAGE_DIR"
rm -f  "$REPO_ROOT/${ARTIFACT_NAME}".zip "$REPO_ROOT/${ARTIFACT_NAME}".tar.gz

# ── Restore ──────────────────────────────────────────────────────────────────
echo "==> dotnet restore"
dotnet restore "$PROJECT" \
    -p:DesktopOnly=true

# ── Publish (self-contained, linux-x64) ──────────────────────────────────────
echo
echo "==> dotnet publish (Release, linux-x64, self-contained)"
dotnet publish "$PROJECT" \
    --configuration Release \
    --framework net10.0 \
    --runtime linux-x64 \
    --self-contained true \
    -p:DesktopOnly=true \
    --output "$PUBLISH_DIR"

# ── Stage zip layout ─────────────────────────────────────────────────────────
echo
echo "==> Staging zip layout"
mkdir -p "$STAGE_DIR/app/assets"
cp -r "$PUBLISH_DIR/." "$STAGE_DIR/app/"
cp "$REPO_ROOT/scripts/install/linux/install-linux.sh" "$STAGE_DIR/"
cp "$REPO_ROOT/assets/icon-256.png"              "$STAGE_DIR/app/assets/"
cp "$REPO_ROOT/assets/icon.svg"                  "$STAGE_DIR/app/assets/"

# ── Archive ──────────────────────────────────────────────────────────────────
echo
if command -v zip &>/dev/null; then
    echo "==> Creating ${ARTIFACT_NAME}.zip"
    ARCHIVE_OUT="$REPO_ROOT/${ARTIFACT_NAME}.zip"
    rm -f "$ARCHIVE_OUT"
    (cd "$REPO_ROOT" && zip -r "$ARCHIVE_OUT" "publish/linux-zip/")
else
    echo "==> zip not found, creating ${ARTIFACT_NAME}.tar.gz instead"
    ARCHIVE_OUT="$REPO_ROOT/${ARTIFACT_NAME}.tar.gz"
    rm -f "$ARCHIVE_OUT"
    tar -czf "$ARCHIVE_OUT" -C "$REPO_ROOT" "publish/linux-zip/"
fi

echo
echo "==> Done: $ARCHIVE_OUT"
