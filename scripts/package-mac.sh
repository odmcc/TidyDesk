#!/usr/bin/env bash
# Builds TidyDesk.app and dist/TidyDesk-mac-<arch>.dmg. Runs on macOS (uses sips, iconutil, codesign, hdiutil).
# Usage:  scripts/package-mac.sh [version] [arm64|x64]      e.g.  scripts/package-mac.sh 3.2.0 arm64
# The app is not notarized (no Apple Developer account). It gets an ad-hoc signature, which Apple Silicon
# requires for any app to start; on first launch the user approves it once (see README, "macOS").
set -euo pipefail

VERSION="${1:-3.2.0}"
ARCH="${2:-arm64}"
case "$ARCH" in arm64|x64) ;; *) echo "Architecture must be arm64 or x64" >&2; exit 1 ;; esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/mac-$ARCH"
APP="$OUT/TidyDesk.app"
DMG="$ROOT/dist/TidyDesk-mac-$ARCH.dmg"

rm -rf "$OUT" "$DMG"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish "$ROOT/src/TidyDesk.Mac/TidyDesk.Mac.csproj" -c Release -r "osx-$ARCH" --self-contained true \
    -p:Version="$VERSION" -p:DebugType=none -o "$APP/Contents/MacOS"

# App icon from the same logo the Windows app uses
ICONSET="$OUT/TidyDesk.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$ROOT/src/TidyDesk/Assets/logo.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$ROOT/src/TidyDesk/Assets/logo.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/TidyDesk.icns"
rm -rf "$ICONSET"

sed "s/__VERSION__/$VERSION/g" "$ROOT/src/TidyDesk.Mac/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"

# Ad-hoc signature: every file in Contents/MacOS first, then the bundle itself.
find "$APP/Contents/MacOS" -type f -exec codesign --force --sign - {} \;
codesign --force --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# Disk image with an Applications shortcut, so the app can be dragged into place.
STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "TidyDesk" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
rm -rf "$STAGE"

echo "Built $DMG"
