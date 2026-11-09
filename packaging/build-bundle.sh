#!/bin/bash
# Build the edge-arm-bridge bundle: one tar that carries FEX, the x86-64
# Edge, a trimmed x86-64 sysroot, an arm64 Chromium, and the sync service.
#
# This script runs on a BUILD machine, and it is the only place in the
# project that touches a package manager. It downloads .deb files into a
# scratch directory and extracts them. It installs nothing. The machine
# that untars the bundle never runs apt or dnf: install.sh inside the
# bundle only writes per-user files.
#
# Build machine needs: an arm64 Ubuntu with the FEX PPA available to
# apt-get download (download only), curl, dpkg-deb, python3, and the .NET
# 11 SDK. Rebuilds pick up the newest Edge stable and the newest Chrome
# for Testing stable at build time, and the newest FEX the PPA offers.
set -euo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
BUILD=${BUILD_DIR:-$REPO/build}
STAGE=$BUILD/bundle
export DEBIAN_FRONTEND=noninteractive
rm -rf "$STAGE"
mkdir -p "$BUILD"/{apt/lists/partial,apt/cache/archives/partial,pool,debs} "$STAGE"

# --- Versions: latest at build time, recorded in VERSIONS.txt ---
EDGE_DEB=$(curl -fsSL https://packages.microsoft.com/repos/edge-stable/pool/main/m/microsoft-edge-stable/ \
  | grep -oE 'microsoft-edge-stable_[0-9.]+-1_amd64.deb' | sort -V | tail -1)
EDGE_VERSION=$(echo "$EDGE_DEB" | sed -E 's/microsoft-edge-stable_([0-9.]+)-1_amd64.deb/\1/')
CFT_VERSION=$(curl -fsSL https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions.json \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["channels"]["Stable"]["version"])')

# --- FEX: download the arm64 .deb packages, extract, never install ---
# Self-contained: FEX ships with it's own arm64 C runtime (libc6, libstdc++6,
# libgcc-s1) and a bundled patchelf. The binaries get an $ORIGIN rpath here.
# The interpreter path is absolute and cannot be set until the bundle's
# final location is known, so install.sh patches it there, running patchelf
# through the bundled loader. Nothing of FEX touches the host C library
# after that, which is what makes the same tar work across distributions.
(cd "$BUILD/debs" && apt-get download fex-emu-armv8.2 libc6 libstdc++6 libgcc-s1 patchelf >/dev/null)
FEX_DEB=$(ls "$BUILD"/debs/fex-emu-*.deb | head -1)
mkdir -p "$STAGE/fex"
dpkg-deb -x "$FEX_DEB" "$STAGE/fex"
for d in "$BUILD"/debs/libc6_*.deb "$BUILD"/debs/libstdc++6_*.deb "$BUILD"/debs/libgcc-s1_*.deb "$BUILD"/debs/patchelf_*.deb; do
  dpkg-deb -x "$d" "$STAGE/fex"
done
# FEXConfig is a Qt GUI the bundle does not use. Drop it and it's the only
# reason the .deb mentions Qt at all: FEX and FEXServer link libc, libstdc++,
# libm, and libgcc only.
rm -f "$STAGE/fex/usr/bin/FEXConfig" "$STAGE/fex/usr/bin/FEXRootFSFetcher"
for bin in FEX FEXServer; do
  patchelf --set-rpath '$ORIGIN/../lib/aarch64-linux-gnu' "$STAGE/fex/usr/bin/$bin"
done

# --- Edge and the sysroot pool: amd64 .debs in a scratch apt state ---
curl -fsSL "https://packages.microsoft.com/repos/edge-stable/pool/main/m/microsoft-edge-stable/$EDGE_DEB" -o "$BUILD/debs/$EDGE_DEB"
cat > "$BUILD/apt/sources.list" <<'SRC'
deb [arch=amd64] http://archive.ubuntu.com/ubuntu noble main universe
deb [arch=amd64] http://archive.ubuntu.com/ubuntu noble-updates main universe
deb [arch=amd64 trusted=yes] https://packages.microsoft.com/repos/edge-stable stable main
SRC
APT="apt-get -o Dir::State::Lists=$BUILD/apt/lists -o Dir::Cache=$BUILD/apt/cache -o Dir::Etc::sourcelist=$BUILD/apt/sources.list -o Dir::Etc::sourceparts=/dev/null -o APT::Architecture=amd64"
APT_CACHE="apt-cache -o Dir::State::Lists=$BUILD/apt/lists -o Dir::Cache=$BUILD/apt/cache -o Dir::Etc::sourcelist=$BUILD/apt/sources.list -o Dir::Etc::sourceparts=/dev/null -o APT::Architecture=amd64"
$APT update
# Package list: the library closure of Edge, filtered to library packages,
# plus the providers whose names do not start with 'lib' (zlib1g is the
# famous one) plus the shell, the module data, and the cert store.
$APT_CACHE depends --recurse --no-recommends --no-suggests --no-conflicts --no-breaks --no-replaces --no-enhances microsoft-edge-stable \
  | grep -E '^[a-z0-9]' | sort -u | grep -E '^lib|^fontconfig' > "$BUILD/libs.txt"
cat >> "$BUILD/libs.txt" <<'EXTRA'
ca-certificates
dash
fontconfig-config
fonts-liberation
mesa-libgallium
shared-mime-info
xkb-data
zlib1g
EXTRA
sort -u -o "$BUILD/libs.txt" "$BUILD/libs.txt"
grep -v '^libasound2-data$' "$BUILD/libs.txt" > "$BUILD/libs-batch.txt" || true
(cd "$BUILD/debs" && $APT download $(cat "$BUILD/libs-batch.txt") >/dev/null 2>&1) ||   (cd "$BUILD/debs" && while read -r pkg; do $APT download "$pkg" >/dev/null 2>&1 || echo "build: skipped $pkg"; done < "$BUILD/libs-batch.txt")
mkdir -p "$BUILD/pool"
for d in "$BUILD"/debs/*.deb; do dpkg-deb -x "$d" "$BUILD/pool"; done
mkdir -p "$BUILD/edge-x"
dpkg-deb -x "$BUILD/debs/$EDGE_DEB" "$BUILD/edge-x"
python3 "$REPO/packaging/mk-sysroot.py" "$BUILD/pool" "$BUILD/edge-x/opt/microsoft/msedge" "$BUILD/sysroot-loose"
mkdir -p "$STAGE/image"
mksquashfs "$BUILD/sysroot-loose" "$STAGE/image/edge-image.squashfs" -comp zstd -b 1M -noappend -quiet

# --- Chromium: arm64 Chrome for Testing, same major as Edge by policy ---
CFT_MAJOR=$(echo "$CFT_VERSION" | cut -d. -f1)
EDGE_MAJOR=$(echo "$EDGE_VERSION" | cut -d. -f1)
if [ "$CFT_MAJOR" != "$EDGE_MAJOR" ]; then
  echo "build: Chrome for Testing major $CFT_MAJOR does not match Edge major $EDGE_MAJOR." >&2
  echo "build: pin CFT to the newest $EDGE_MAJOR build in last-known-good-versions-by-milestone.json and rebuild." >&2
  exit 1
fi
mkdir -p "$STAGE/chromium"
curl -fsSL "https://storage.googleapis.com/chrome-for-testing-public/$CFT_VERSION/linux-arm64/chrome-linux-arm64.zip" -o "$BUILD/cft.zip"
unzip -q "$BUILD/cft.zip" -d "$STAGE/chromium"

# --- The bridge service, self-contained for linux-arm64 ---
dotnet publish -c Release -r linux-arm64 --self-contained true -o "$STAGE/bridge" "$REPO/src/EdgeArmBridge"
rm -f "$STAGE/bridge"/*.pdb
mkdir -p "$STAGE/tools"
cp "$REPO/tools/probe_profile.py" "$STAGE/tools/"
cp -r "$REPO/packaging/bundle/." "$STAGE/"
cp "$REPO/edge-arm-bridge.sh" "$STAGE/edge-arm-bridge.sh"
chmod +x "$STAGE"/*.sh "$STAGE/bridge/EdgeArmBridge"

FEX_VERSION=$(basename "$FEX_DEB" | sed -E 's/fex-emu-[a-z0-9.]+_([0-9]+)-.*/\1/')
{
  echo "edge-arm-bridge bundle"
  echo "built: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "fex: $FEX_VERSION (fex-emu-armv8.2 .deb, extracted, self-contained with bundled arm64 C runtime)"
  echo "edge: $EDGE_VERSION amd64 (microsoft-edge-stable .deb, extracted)"
  echo "chromium: Chrome for Testing $CFT_VERSION linux-arm64"
  echo "bridge: .NET 11, self-contained linux-arm64 publish"
  echo "image: edge-image.squashfs, Ubuntu noble amd64 library closure plus Edge, trimmed by packaging/mk-sysroot.py"
} > "$STAGE/VERSIONS.txt"

OUT="$BUILD/edge-arm-bridge-$EDGE_VERSION.tar.gz"
tar -czf "$OUT" -C "$BUILD" bundle
mv "$OUT" "$BUILD/edge-arm-bridge.tar.gz"
OUT="$BUILD/edge-arm-bridge.tar.gz"
sha256sum "$OUT" > "$OUT.sha256"
echo "built: $OUT ($(du -h "$OUT" | cut -f1))"
cat "$STAGE/VERSIONS.txt"
