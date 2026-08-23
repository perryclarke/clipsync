#!/usr/bin/env bash
# Build a self-contained ClipSync and wrap it in a .deb.
# Output: dist/clipsync_<version>_<arch>.deb (relative to the repo root).
#
# A .deb rather than an AppImage or a Flatpak: this is a background daemon,
# so what matters is installing a systemd --user unit and an autostart entry
# and declaring the shared libraries it dlopens. AppImage has no mechanism
# for any of that, and a Flatpak sandbox interferes with both the X selection
# and raw multicast sockets, which are the two things the app is built on.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DIST="$REPO_ROOT/dist"

ARCH="${1:-amd64}"
case "$ARCH" in
    amd64) RID=linux-x64 ;;
    arm64) RID=linux-arm64 ;;
    *) echo "unknown architecture: $ARCH (expected amd64 or arm64)" >&2; exit 1 ;;
esac

VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$SCRIPT_DIR/ClipSync.Linux.csproj")"
STAGING="$(mktemp -d -t clipsync-deb-XXXXXX)"
trap 'rm -rf "$STAGING"' EXIT
# mktemp gives 0700; dpkg-deb would bake that into the package root and
# every file would land unreadable for anyone but root.
chmod 755 "$STAGING"

echo "Building ClipSync $VERSION for $ARCH ($RID)..."

# Self-contained so the .deb does not depend on a system .NET, which Ubuntu
# does not ship at a predictable version.
dotnet publish "$SCRIPT_DIR/ClipSync.Linux.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false \
    -o "$STAGING/opt/clipsync" >/dev/null

# The published host is named after AssemblyName.
chmod 755 "$STAGING/opt/clipsync/clipsync"

# Debug symbols are not part of a release package.
rm -f "$STAGING/opt/clipsync"/*.pdb

install -Dm644 "$SCRIPT_DIR/packaging/clipsync.service" \
    "$STAGING/usr/lib/systemd/user/clipsync.service"
install -Dm644 "$SCRIPT_DIR/packaging/clipsync.desktop" \
    "$STAGING/etc/xdg/autostart/clipsync.desktop"

mkdir -p "$STAGING/usr/bin"
ln -sf /opt/clipsync/clipsync "$STAGING/usr/bin/clipsync"

INSTALLED_KB="$(du -sk "$STAGING" | cut -f1)"

mkdir -p "$STAGING/DEBIAN"
cat > "$STAGING/DEBIAN/control" <<EOF
Package: clipsync
Version: $VERSION
Section: utils
Priority: optional
Architecture: $ARCH
Depends: libx11-6, libxfixes3, libxcb1, libxcb-xfixes0, avahi-daemon, dbus
Recommends: gnome-shell-extension-appindicator
Installed-Size: $INSTALLED_KB
Maintainer: ClipSync
Description: LAN-only encrypted clipboard sync
 Mirrors the clipboard between macOS, Windows and Linux machines on the
 same subnet. Discovery is mDNS, transport is mutually authenticated
 TLS 1.3, and nothing leaves the local network.
 .
 The tray icon needs a StatusNotifierItem host. On GNOME that means the
 AppIndicator extension, which is why it is recommended here; without one
 the daemon still syncs, it simply has no icon.
EOF

# The trigger matters: a systemd --user unit is not picked up until the
# user daemon rereads its units, and enabling it per-user cannot be done
# from a system-wide postinst.
cat > "$STAGING/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    systemctl --global enable clipsync.service >/dev/null 2>&1 || true
    echo "ClipSync installed. It starts at your next login, or now with:"
    echo "  systemctl --user daemon-reload && systemctl --user start clipsync"
fi
EOF
chmod 755 "$STAGING/DEBIAN/postinst"

cat > "$STAGING/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "remove" ]; then
    systemctl --global disable clipsync.service >/dev/null 2>&1 || true
fi
EOF
chmod 755 "$STAGING/DEBIAN/prerm"

mkdir -p "$DIST"
DEB="$DIST/clipsync_${VERSION}_${ARCH}.deb"
rm -f "$DEB"
fakeroot dpkg-deb --build "$STAGING" "$DEB" >/dev/null

echo "Wrote $DEB"
dpkg-deb --info "$DEB" | sed -n '2,12p'
