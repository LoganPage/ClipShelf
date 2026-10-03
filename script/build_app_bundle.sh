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

(cd "$ROOT_DIR" && swift script/generate_app_icon.swift)
for variant in 1 2 3 4; do
  iconutil -c icns "$ROOT_DIR/Assets/AppIcon${variant}.iconset" -o "$ROOT_DIR/Assets/AppIcon${variant}.icns"
done
cp "$ROOT_DIR/Assets/AppIcon1.icns" "$ICON_SOURCE"

cp "$ICON_SOURCE" "$APP_RESOURCES/$ICON_FILE"
cp "$ROOT_DIR"/Assets/AppIcon{1,2,3,4}.icns "$APP_RESOURCES/"
cp "$ROOT_DIR"/Assets/AppStatusIcon.png "$APP_RESOURCES/"

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
codesign --force --deep --sign - "$APP_BUNDLE" >/dev/null
codesign --verify --deep --strict --verbose=1 "$APP_BUNDLE" >/dev/null
mkdir -p "$DIST_DIR"
ditto "$APP_BUNDLE" "$DIST_APP_BUNDLE" >/dev/null 2>&1 || true
echo "$APP_BUNDLE"
