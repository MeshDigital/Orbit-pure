#!/usr/bin/env bash
set -euo pipefail

# Packages a published ORBIT Linux directory into a self-contained AppImage (#458).
# Usage: ./Tools/packaging/linux/build-appimage.sh <publish_dir> <version> <output_appimage>

PUBLISH_DIR="$1"
VERSION="$2"
OUTPUT_APPIMAGE="$3"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

APPDIR="$STAGE_DIR/ORBIT.AppDir"
mkdir -p "$APPDIR/usr/bin"

cp -R "$PUBLISH_DIR"/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/ORBIT"

if [ -f "$REPO_ROOT/app_icon.png" ]; then
    cp "$REPO_ROOT/app_icon.png" "$APPDIR/orbit.png"
    cp "$REPO_ROOT/app_icon.png" "$APPDIR/.DirIcon"
fi

cat > "$APPDIR/orbit.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ORBIT
Comment=Soulseek client and DJ prep workstation
Exec=ORBIT
Icon=orbit
Categories=AudioVideo;Audio;Music;
Terminal=false
X-AppImage-Version=${VERSION}
EOF

cat > "$APPDIR/AppRun" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "${0}")")"
exec "$HERE/usr/bin/ORBIT" "$@"
EOF
chmod +x "$APPDIR/AppRun"

APPIMAGETOOL="${APPIMAGETOOL:-appimagetool}"
if ! command -v "$APPIMAGETOOL" >/dev/null 2>&1; then
    echo "Downloading appimagetool..."
    APPIMAGETOOL="$STAGE_DIR/appimagetool"
    curl -fsSL "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" -o "$APPIMAGETOOL"
    chmod +x "$APPIMAGETOOL"
fi

mkdir -p "$(dirname "$OUTPUT_APPIMAGE")"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" "$APPDIR" "$OUTPUT_APPIMAGE"
chmod +x "$OUTPUT_APPIMAGE"
echo "Created AppImage: $OUTPUT_APPIMAGE"
