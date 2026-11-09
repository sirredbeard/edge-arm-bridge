#!/bin/sh
# Build the edge-arm-bridge RPM set from a staged bundle.
# Usage: build-rpm.sh --bundle DIR --gui DIR --version X.Y.Z --out DIR
# Runs inside a Fedora container (rpmbuild lives there). The bundle staging
# comes from packaging/build-bundle.sh; --gui is a Native AOT publish of
# src/EdgeArmBridge.Configurator. Output: the RPMs plus a .repo file.
#
# GitHub Pages serves one file up to 100 MB, so the Edge image (about
# 219 MB as a squashfs) ships split across subpackages of at most 88 MB
# each. dnf pulls them all as dependencies of the main package, whose
# %post script joins the parts and checks the hash.
set -eu
BUNDLE=""; GUI=""; VERSION=""; OUT=""
while [ $# -gt 0 ]; do
    case "$1" in
        --bundle) BUNDLE="$2"; shift 2 ;;
        --gui) GUI="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 1 ;;
    esac
done
[ -n "$BUNDLE" ] && [ -n "$GUI" ] && [ -n "$VERSION" ] && [ -n "$OUT" ] || { echo "usage: $0 --bundle DIR --gui DIR --version X.Y.Z --out DIR" >&2; exit 1; }
IMAGE="$BUNDLE/image/edge-image.squashfs"
[ -f "$IMAGE" ] || { echo "no Edge image at $IMAGE" >&2; exit 1; }
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/stage/parts" "$WORK/rpmbuild" "$OUT"
split -b 84M -d -a 1 "$IMAGE" "$WORK/stage/parts/part-"
PART_COUNT="$(ls "$WORK/stage/parts" | wc -l)"
echo "image split into $PART_COUNT parts"
IMAGE_SHA256="$(sha256sum "$IMAGE" | awk '{print $1}')"
# Edge product logos for the desktop entry icon, lifted out of the image.
mkdir -p "$WORK/stage/edge-icons"
for size in 256 128 64 48; do
    unsquashfs -q -f -d "$WORK/sq-$size" "$IMAGE" "opt/microsoft/msedge/product_logo_$size.png" >/dev/null 2>&1 || true
    if [ -f "$WORK/sq-$size/opt/microsoft/msedge/product_logo_$size.png" ]; then
        cp "$WORK/sq-$size/opt/microsoft/msedge/product_logo_$size.png" "$WORK/stage/edge-icons/microsoft-edge-$size.png"
    fi
done
ls "$WORK/stage/edge-icons" | grep -q . || { echo "no Edge logos found in the image" >&2; exit 1; }
# Spec, generated because the part count varies with the image size.
{
cat <<EOF
%global debug_package %{nil}
%global __os_install_post %{nil}
%define _binary_payload w19.zstdio

Name:           edge-arm-bridge
Version:        $VERSION
Release:        1%{?dist}
Summary:        Mirror a Microsoft Edge profile (under FEX) to a native arm64 Chromium
License:        Apache-2.0
URL:            https://github.com/sirredbeard/edge-arm-bridge
BuildArch:      aarch64
AutoReqProv:    no
Requires:       edge-arm-bridge-fex = %{version}-%{release}
Requires:       python3
Requires:       libsecret
Requires:       gtk4
Requires:       fuse3
Requires:       systemd
Requires:       (chromium or google-chrome-stable)
EOF
for i in $(seq 0 $((PART_COUNT - 1))); do echo "Requires:       edge-arm-bridge-edge-image-$i = %{version}-%{release}"; done
cat <<'EOF'
Source0:        bundle.tar

%description
Edge runs x86-64 under FEX so a Microsoft account can sign in. This
package mirrors that profile to a native arm64 Chromium-family browser
and keeps it fresh: a timer wakes Edge headless, lets Microsoft sync
land, then mirrors. Install puts the service, the GUI settings app, the
desktop entries, and (through its subpackages) FEX and the Edge image
in place. Profiles and config live in the user's home.

%package fex
Summary:        Self-contained FEX emulator for edge-arm-bridge
AutoReqProv:    no

%description fex
The FEX x86-64 emulator, extracted (never installed on its own) and
carrying its own arm64 C runtime. The main package points its
interpreter at the bundled loader after install.
EOF
for i in $(seq 0 $((PART_COUNT - 1))); do
cat <<EOF

%package edge-image-$i
Summary:        Edge rootfs image for edge-arm-bridge, part $i
AutoReqProv:    no

%description edge-image-$i
One part of the trimmed Edge squashfs image. GitHub Pages serves one
file up to 100 MB, so the image ships in parts; the main package joins
them at install time and checks the hash.
EOF
done
cat <<'EOF'

%prep
%setup -q -n bundle

%build
:

%install
ROOT=%{buildroot}/usr/lib/edge-arm-bridge
mkdir -p "$ROOT" %{buildroot}/usr/bin %{buildroot}/usr/lib/systemd/user %{buildroot}/usr/lib/systemd/user-preset \
    %{buildroot}/usr/share/applications \
    %{buildroot}/usr/share/icons/hicolor/scalable/apps \
    %{buildroot}/usr/share/edge-arm-bridge/image-parts \
    "$ROOT/image" "$ROOT/bridge" "$ROOT/configurator" "$ROOT/tools" "$ROOT/fex"
cp -a stage/fex/. "$ROOT/fex/"
cp -a stage/bridge/. "$ROOT/bridge/"
cp -a stage/configurator/. "$ROOT/configurator/"
cp stage/tools/probe_profile.py "$ROOT/tools/"
cp stage/files/ensure-config.sh "$ROOT/ensure-config.sh"
for w in edge-arm-bridge-edge edge-arm-bridge-sync edge-arm-bridge-wake edge-arm-bridge-configurator; do
    install -m 0755 "stage/files/$w" "%{buildroot}/usr/bin/$w"
done
install -m 0644 stage/files/edge-arm-bridge-wake.service stage/files/edge-arm-bridge-wake.timer %{buildroot}/usr/lib/systemd/user/
install -m 0644 stage/files/90-edge-arm-bridge.preset %{buildroot}/usr/lib/systemd/user-preset/
install -m 0644 stage/files/microsoft-edge-fex.desktop stage/files/edge-arm-bridge-configurator.desktop %{buildroot}/usr/share/applications/
install -m 0644 stage/icons/edge-arm-bridge-sync.svg %{buildroot}/usr/share/icons/hicolor/scalable/apps/edge-arm-bridge-sync.svg
for png in stage/edge-icons/microsoft-edge-*.png; do
    size="$(basename "$png" | sed -E 's/microsoft-edge-([0-9]+).png/\1/')"
    mkdir -p "%{buildroot}/usr/share/icons/hicolor/${size}x${size}/apps"
    install -m 0644 "$png" "%{buildroot}/usr/share/icons/hicolor/${size}x${size}/apps/microsoft-edge.png"
done
cp stage/parts/part-* %{buildroot}/usr/share/edge-arm-bridge/image-parts/
echo "IMAGE_SHA256_PLACEHOLDER" > "$ROOT/image/image.sha256"
cp stage/VERSIONS.txt "$ROOT/VERSIONS.txt" 2>/dev/null || true

%post
# Register the wake timer for every user on first install. The sync runs
# as the user (it needs the user's keyring), so the unit is a user unit;
# --global is how a package turns a user unit on by default. The settings
# app is the off switch: a per-user disable beats this, and upgrades
# never re-enable (this block only runs when $1 is 1).
if [ "$1" -eq 1 ]; then
    systemctl --global enable edge-arm-bridge-wake.timer >/dev/null 2>&1 || true
fi

%posttrans
ROOT=/usr/lib/edge-arm-bridge
# Join the Edge image parts and check the result.
IMAGE_FILE="$ROOT/image/edge-image.squashfs"
if [ ! -f "$IMAGE_FILE" ] || ! echo "$(cat "$ROOT/image/image.sha256")  $IMAGE_FILE" | sha256sum -c - >/dev/null 2>&1; then
    cat /usr/share/edge-arm-bridge/image-parts/part-* > "$IMAGE_FILE.tmp"
    if echo "$(cat "$ROOT/image/image.sha256")  $IMAGE_FILE.tmp" | sha256sum -c - >/dev/null 2>&1; then
        mv "$IMAGE_FILE.tmp" "$IMAGE_FILE"
    else
        rm -f "$IMAGE_FILE.tmp"
        echo "edge-arm-bridge: Edge image parts did not join to the expected hash" >&2
    fi
fi

%postun
if [ "$1" -eq 0 ]; then
    rm -f /usr/lib/edge-arm-bridge/image/edge-image.squashfs
fi

%files
/usr/lib/edge-arm-bridge/bridge
/usr/lib/edge-arm-bridge/configurator
/usr/lib/edge-arm-bridge/tools
/usr/lib/edge-arm-bridge/ensure-config.sh
/usr/lib/edge-arm-bridge/VERSIONS.txt
/usr/lib/edge-arm-bridge/image/image.sha256
/usr/bin/edge-arm-bridge-edge
/usr/bin/edge-arm-bridge-sync
/usr/bin/edge-arm-bridge-wake
/usr/bin/edge-arm-bridge-configurator
/usr/lib/systemd/user/edge-arm-bridge-wake.service
/usr/lib/systemd/user/edge-arm-bridge-wake.timer
/usr/lib/systemd/user-preset/90-edge-arm-bridge.preset
/usr/share/applications/microsoft-edge-fex.desktop
/usr/share/applications/edge-arm-bridge-configurator.desktop
/usr/share/icons/hicolor/scalable/apps/edge-arm-bridge-sync.svg
/usr/share/icons/hicolor/*/apps/microsoft-edge.png
%dir /usr/lib/edge-arm-bridge/image
%ghost /usr/lib/edge-arm-bridge/image/edge-image.squashfs

%files fex
/usr/lib/edge-arm-bridge/fex
EOF
for i in $(seq 0 $((PART_COUNT - 1))); do
printf '\n%%files edge-image-%s\n/usr/share/edge-arm-bridge/image-parts/part-%s\n' "$i" "$i"
done
} > "$WORK/edge-arm-bridge.spec"
sed -i "s/IMAGE_SHA256_PLACEHOLDER/$IMAGE_SHA256/" "$WORK/edge-arm-bridge.spec"
# Stage the bundle tree the spec expects.
mkdir -p "$WORK/bundle/stage"
cp -a "$BUNDLE/fex" "$WORK/bundle/stage/fex"
# The RPM always installs to /usr/lib/edge-arm-bridge, so the interpreter
# can be set here at build time instead of in a scriptlet. The tar bundle
# still patches at install time because its path varies.
for bin in FEX FEXServer; do
    patchelf --set-interpreter "/usr/lib/edge-arm-bridge/fex/usr/lib/aarch64-linux-gnu/ld-linux-aarch64.so.1" "$WORK/bundle/stage/fex/usr/bin/$bin"
done
cp -a "$BUNDLE/bridge" "$WORK/bundle/stage/bridge"
mkdir -p "$WORK/bundle/stage/configurator"
cp -a "$GUI/." "$WORK/bundle/stage/configurator/"
rm -f "$WORK/bundle/stage/configurator"/*.dbg
mkdir -p "$WORK/bundle/stage/tools" "$WORK/bundle/stage/files" "$WORK/bundle/stage/icons" "$WORK/bundle/stage/parts" "$WORK/bundle/stage/edge-icons"
cp "$BUNDLE/tools/probe_profile.py" "$WORK/bundle/stage/tools/"
cp "$(dirname "$0")/files"/* "$WORK/bundle/stage/files/"
cp "$(dirname "$0")/icons/edge-arm-bridge-sync.svg" "$WORK/bundle/stage/icons/"
cp "$WORK/stage/parts"/* "$WORK/bundle/stage/parts/"
cp "$WORK/stage/edge-icons"/* "$WORK/bundle/stage/edge-icons/"
cp "$BUNDLE/VERSIONS.txt" "$WORK/bundle/stage/" 2>/dev/null || true
tar -cf "$WORK/rpmbuild/SOURCES/bundle.tar" -C "$WORK" bundle 2>/dev/null || { mkdir -p "$WORK/rpmbuild/SOURCES"; tar -cf "$WORK/rpmbuild/SOURCES/bundle.tar" -C "$WORK" bundle; }
mkdir -p "$WORK/rpmbuild"/{BUILD,RPMS,SPECS}
cp "$WORK/edge-arm-bridge.spec" "$WORK/rpmbuild/SPECS/"
HOME="$WORK" rpmbuild -bb --define "_topdir $WORK/rpmbuild" "$WORK/rpmbuild/SPECS/edge-arm-bridge.spec" 2>&1 | tail -4
cp "$WORK/edge-arm-bridge.spec" "$OUT/" ; find "$WORK/rpmbuild/RPMS" -name '*.rpm' -exec cp {} "$OUT/" \;
ls -la "$OUT"
