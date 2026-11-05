#!/usr/bin/env bash
set -euo pipefail

APP_NAME="ClipShelf"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_APP="$("$ROOT_DIR/script/build_app_bundle.sh")"
INSTALL_DIR="$HOME/Applications"
INSTALLED_APP="$INSTALL_DIR/$APP_NAME.app"
DESKTOP_LINK="$HOME/Desktop/$APP_NAME.app"

pkill -x "$APP_NAME" >/dev/null 2>&1 || true
pkill -x "ClipShelfLite" >/dev/null 2>&1 || true

mkdir -p "$INSTALL_DIR"
rm -rf "$INSTALLED_APP"
rm -rf "$INSTALL_DIR/ClipShelfLite.app"
ditto "$SOURCE_APP" "$INSTALLED_APP"
xattr -cr "$INSTALLED_APP" >/dev/null 2>&1 || true
# A fixed local identity keeps the app's signature the same across builds,
# so macOS remembers granted permissions (folder access and so on) instead
# of asking again after every install. Falls back to ad-hoc signing.
SIGN_IDENTITY="${CLIPSHELF_SIGN_IDENTITY:-ClipShelf Local Signing}"
if security find-identity -p codesigning 2>/dev/null | grep -q "\"$SIGN_IDENTITY\""; then
  SIGN_WITH="$SIGN_IDENTITY"
else
  SIGN_WITH="-"
fi
codesign --force --deep --sign "$SIGN_WITH" "$INSTALLED_APP" >/dev/null 2>&1 || true

/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
  -f "$INSTALLED_APP" >/dev/null 2>&1 || true

rm -f "$DESKTOP_LINK"
rm -f "$HOME/Desktop/ClipShelfLite.app"
ln -s "$INSTALLED_APP" "$DESKTOP_LINK"

open "$INSTALLED_APP"
echo "$INSTALLED_APP"
