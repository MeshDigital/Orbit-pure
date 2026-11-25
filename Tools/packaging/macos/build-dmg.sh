#!/usr/bin/env bash
set -euo pipefail

# Packages a published ORBIT macOS directory into ORBIT.app + ORBIT-<version>-<rid>.dmg (#458).
# Signs with Hardened Runtime + notarises/staples when Apple Developer env vars are set;
# otherwise ad-hoc signs so local/unsigned builds still launch cleanly.
#
# Usage: ./Tools/packaging/macos/build-dmg.sh <publish_dir> <version> <rid> <output_dmg>

PUBLISH_DIR="$1"
VERSION="$2"
RID="$3"
OUTPUT_DMG="$4"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

APP_DIR="$STAGE_DIR/ORBIT.app"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

# Copy published payload
cp -R "$PUBLISH_DIR"/. "$APP_DIR/Contents/MacOS/"
chmod +x "$APP_DIR/Contents/MacOS/ORBIT"

# Info.plist
sed "s/__VERSION__/${VERSION}/g" "$SCRIPT_DIR/Info.plist.template" > "$APP_DIR/Contents/Info.plist"

# Build .icns from app_icon.png when iconutil/sips are available
if [ -f "$REPO_ROOT/app_icon.png" ] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    ICONSET="$STAGE_DIR/ORBIT.iconset"
    mkdir -p "$ICONSET"
    for sz in 16 32 128 256 512; do
        sips -z "$sz" "$sz" "$REPO_ROOT/app_icon.png" --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null
        sz2=$((sz * 2))
        sips -z "$sz2" "$sz2" "$REPO_ROOT/app_icon.png" --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP_DIR/Contents/Resources/ORBIT.icns"
fi

# Code signing (Developer ID when configured; ad-hoc otherwise)
if [ -n "${APPLE_SIGN_IDENTITY:-}" ]; then
    echo "Signing native binaries and bundle with Developer ID..."
    find "$APP_DIR/Contents/MacOS" -type f \( -name "*.dylib" -o -name "*.so" -o -perm +111 \) | while read -r bin; do
        codesign --force --options runtime --timestamp --entitlements "$SCRIPT_DIR/entitlements.plist" --sign "$APPLE_SIGN_IDENTITY" "$bin"
    done
    codesign --force --options runtime --timestamp --entitlements "$SCRIPT_DIR/entitlements.plist" --sign "$APPLE_SIGN_IDENTITY" "$APP_DIR"
else
    echo "APPLE_SIGN_IDENTITY not set — applying ad-hoc signature."
    codesign --force --deep --sign - "$APP_DIR" || true
fi

# Create DMG with /Applications symlink
DMG_ROOT="$STAGE_DIR/dmg"
mkdir -p "$DMG_ROOT"
cp -R "$APP_DIR" "$DMG_ROOT/ORBIT.app"
ln -s /Applications "$DMG_ROOT/Applications"

mkdir -p "$(dirname "$OUTPUT_DMG")"
rm -f "$OUTPUT_DMG"
hdiutil create -volname "ORBIT ${VERSION} (${RID})" -srcfolder "$DMG_ROOT" -ov -format UDZO "$OUTPUT_DMG"

# Sign + notarise + staple DMG when Apple credentials are configured
if [ -n "${APPLE_SIGN_IDENTITY:-}" ]; then
    codesign --force --timestamp --sign "$APPLE_SIGN_IDENTITY" "$OUTPUT_DMG"
fi

if [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_SPECIFIC_PASSWORD:-}" ]; then
    echo "Submitting $OUTPUT_DMG for Apple notarisation..."
    xcrun notarytool submit "$OUTPUT_DMG" \
        --apple-id "$APPLE_ID" \
        --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_SPECIFIC_PASSWORD" \
        --wait
    xcrun stapler staple "$OUTPUT_DMG"
    echo "Notarised and stapled: $OUTPUT_DMG"
fi
