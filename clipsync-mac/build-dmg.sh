#!/usr/bin/env bash
# Build a release ClipSync.app and wrap it in a drag-to-Applications DMG.
# Output: dist/ClipSync.dmg (relative to the repo root).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
APP="$SCRIPT_DIR/ClipSync.app"
DIST="$REPO_ROOT/dist"
DMG_OUT="$DIST/ClipSync.dmg"
STAGING="$(mktemp -d -t clipsync-dmg-staging)"
DMG_RW="$(mktemp -t clipsync-dmg-rw).dmg"
VOLNAME="ClipSync"

DEV_NODE=""
cleanup() {
    [[ -n "$DEV_NODE" ]] && hdiutil detach "$DEV_NODE" -quiet -force >/dev/null 2>&1 || true
    rm -rf "$STAGING" "$DMG_RW"
}
trap cleanup EXIT

# Pick the swift toolchain. The Command Line Tools build of swift sometimes
# can't read the package's manifest (PackageDescription mismatch), so prefer
# Xcode's swift if available.
SWIFT_BIN="swift"
for cand in \
    "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift" \
    /Applications/Xcode-*.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift
do
    [[ -x "$cand" ]] && { SWIFT_BIN="$cand"; break; }
done

echo "Building release with: $SWIFT_BIN"
"$SWIFT_BIN" build -c release --package-path "$SCRIPT_DIR"

echo "Updating app bundle..."
cp "$SCRIPT_DIR/.build/release/ClipSync" "$APP/Contents/MacOS/ClipSync"
# Prefer a real signing identity over ad-hoc. Ad-hoc gives every build a
# different code identity, so the keychain re-prompts for the TLS identity
# key on each rebuild (several dialogs per launch); a stable identity is
# prompted once ("Always Allow") and never again.
SIGN_ID="$(security find-identity -v -p codesigning 2>/dev/null \
    | awk -F'"' '/Apple Development|Developer ID Application/ {print $2; exit}')"
codesign --force --sign "${SIGN_ID:--}" "$APP"
echo "Signed with: ${SIGN_ID:-ad-hoc}"

echo "Staging DMG contents..."
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
mkdir "$STAGING/.background"
cp "$SCRIPT_DIR/dmg/background.tiff" "$STAGING/.background/background.tiff"

mkdir -p "$DIST"
rm -f "$DMG_OUT"

echo "Creating writable DMG..."
hdiutil create -volname "$VOLNAME" -srcfolder "$STAGING" \
    -fs HFS+ -format UDRW -size 32m "$DMG_RW" >/dev/null

echo "Attaching..."
ATTACH_OUT="$(hdiutil attach "$DMG_RW" -nobrowse -noautoopen)"
DEV_NODE="$(printf '%s\n' "$ATTACH_OUT" | awk '/Apple_HFS/ {print $1; exit}')"
MOUNT_POINT="$(printf '%s\n' "$ATTACH_OUT" | awk '/Apple_HFS/ {sub($1FS$2FS,""); print; exit}')"
DISK_NAME="$(basename "$MOUNT_POINT")"

echo "Setting Finder layout..."
osascript <<EOF
tell application "Finder"
    tell disk "$DISK_NAME"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        -- content area 660x400, matching the background image
        set the bounds of container window to {200, 120, 860, 548}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 128
        set text size of viewOptions to 13
        set background picture of viewOptions to file ".background:background.tiff"
        set position of item "ClipSync.app" of container window to {165, 185}
        set position of item "Applications" of container window to {495, 185}
        update without registering applications
        delay 1
        close
    end tell
end tell
EOF

sync
hdiutil detach "$DEV_NODE" -quiet
DEV_NODE=""

echo "Compressing to $DMG_OUT..."
hdiutil convert "$DMG_RW" -format UDZO -imagekey zlib-level=9 -o "$DMG_OUT" >/dev/null

echo "Done: $(ls -lh "$DMG_OUT" | awk '{print $5, $9}')"
