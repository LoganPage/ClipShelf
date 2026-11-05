#!/usr/bin/env bash
set -euo pipefail

APP_NAME="ClipShelf"
BUNDLE_ID="local.codex.ClipShelf"
MIN_SYSTEM_VERSION="13.0"
APP_VERSION="1.4.1"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST_DIR="$ROOT_DIR/dist"
BUILD_APP_ROOT="${TMPDIR:-/tmp}/clipshelf-app-bundle"
APP_BUNDLE="$BUILD_APP_ROOT/$APP_NAME.app"
DIST_APP_BUNDLE="$DIST_DIR/$APP_NAME.app"
APP_CONTENTS="$APP_BUNDLE/Contents"
APP_MACOS="$APP_CONTENTS/MacOS"
APP_RESOURCES="$APP_CONTENTS/Resources"
INFO_PLIST="$APP_CONTENTS/Info.plist"
ICON_FILE="AppIcon.icns"
ICON_SOURCE="$ROOT_DIR/Assets/$ICON_FILE"

export CLANG_MODULE_CACHE_PATH="$ROOT_DIR/.build/ModuleCache"
export SWIFTPM_MODULECACHE_OVERRIDE="$ROOT_DIR/.build/ModuleCache"
mkdir -p "$CLANG_MODULE_CACHE_PATH"

swift build --disable-sandbox -c release --scratch-path "$ROOT_DIR/.build" >&2
BUILD_BINARY="$(swift build --disable-sandbox -c release --scratch-path "$ROOT_DIR/.build" --show-bin-path)/$APP_NAME"

rm -rf "$BUILD_APP_ROOT" "$DIST_APP_BUNDLE"
mkdir -p "$APP_MACOS" "$APP_RESOURCES"
cp "$BUILD_BINARY" "$APP_MACOS/$APP_NAME"
chmod +x "$APP_MACOS/$APP_NAME"

# Liquid Glass icons (generated with ChatGPT, light and dark). The bundle
# icon (Finder, Dock when not running) is the light one; the running app
# switches its Dock icon with the appearance (see AppDelegate).
GLASS_ICONS="$ROOT_DIR/Assets/GlassIcon"
ICONSET="$BUILD_APP_ROOT/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$GLASS_ICONS/AppIcon-light.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$GLASS_ICONS/AppIcon-light.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$ICON_SOURCE"
rm -rf "$ICONSET"

cp "$ICON_SOURCE" "$APP_RESOURCES/$ICON_FILE"
cp "$GLASS_ICONS/AppIcon-light.png" "$APP_RESOURCES/AppIconLight.png"
cp "$GLASS_ICONS/AppIcon-dark.png" "$APP_RESOURCES/AppIconDark.png"
cp "$GLASS_ICONS/StatusIcon.png" "$APP_RESOURCES/AppStatusIcon.png"
cp "$GLASS_ICONS/StatusIcon@2x.png" "$APP_RESOURCES/AppStatusIcon@2x.png"

cat >"$INFO_PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>$APP_NAME</string>
  <key>CFBundleIdentifier</key>
  <string>$BUNDLE_ID</string>
  <key>CFBundleName</key>
  <string>ClipShelf</string>
  <key>CFBundleDisplayName</key>
  <string>ClipShelf</string>
  <key>CFBundleIconFile</key>
  <string>$ICON_FILE</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$APP_VERSION</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>LSMinimumSystemVersion</key>
  <string>$MIN_SYSTEM_VERSION</string>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
</dict>
</plist>
PLIST

xattr -cr "$APP_BUNDLE" >/dev/null 2>&1 || true
xattr -d com.apple.FinderInfo "$APP_BUNDLE" >/dev/null 2>&1 || true
xattr -d 'com.apple.fileprovider.fpfs#P' "$APP_BUNDLE" >/dev/null 2>&1 || true
xattr -cr "$APP_BUNDLE" >/dev/null 2>&1 || true
# A fixed local identity keeps the app's signature the same across builds,
# so macOS remembers granted permissions (folder access and so on) instead
# of asking again after every install. Falls back to ad-hoc signing.
SIGN_IDENTITY="${CLIPSHELF_SIGN_IDENTITY:-ClipShelf Local Signing}"
if security find-identity -p codesigning 2>/dev/null | grep -q "\"$SIGN_IDENTITY\""; then
  SIGN_WITH="$SIGN_IDENTITY"
else
  SIGN_WITH="-"
fi
codesign --force --deep --sign "$SIGN_WITH" "$APP_BUNDLE" >/dev/null
codesign --verify --deep --strict --verbose=1 "$APP_BUNDLE" >/dev/null
mkdir -p "$DIST_DIR"
ditto "$APP_BUNDLE" "$DIST_APP_BUNDLE" >/dev/null 2>&1 || true
echo "$APP_BUNDLE"
