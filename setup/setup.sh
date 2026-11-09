#!/bin/bash
# SUPERSEDED by the bundle route in packaging/. Kept for reference.
# edge-arm-bridge setup, arm64 Ubuntu host.
# Installs FEX and the amd64 Edge build the edge-fex way, fetches a native
# arm64 Chromium build, writes the sync config, and builds the service.
# Run it in a desktop session. The Microsoft sign-in at the end needs one.
set -euo pipefail

EDGE_VERSION=155.0.4283.45-1
EDGE_DEB=microsoft-edge-stable_${EDGE_VERSION}_amd64.deb
EDGE_URL=https://packages.microsoft.com/repos/edge-stable/pool/main/m/microsoft-edge-stable/${EDGE_DEB}
CFT_VERSION=155.0.8059.39
CFT_URL=https://storage.googleapis.com/chrome-for-testing-public/${CFT_VERSION}/linux-arm64/chrome-linux-arm64.zip
DATA_HOME=${XDG_DATA_HOME:-$HOME/.local/share}/edge-arm-bridge
CONFIG_HOME=${XDG_CONFIG_HOME:-$HOME/.config}/edge-arm-bridge

if [ "$(uname -m)" != "aarch64" ]; then
  echo "This setup targets an arm64 host. Got: $(uname -m)" >&2
  exit 1
fi

# 1. FEX from it's Ubuntu PPA. Same source as the board this was written on:
#    ppa.launchpadcontent.net/fex-emu/fex, package fex-emu-armv8.2, 2611-1.
if ! command -v FEX >/dev/null 2>&1; then
  sudo apt-get update
  sudo apt-get install -y software-properties-common
  sudo add-apt-repository -y ppa:fex-emu/fex
  sudo apt-get update
  sudo apt-get install -y fex-emu-armv8.2 fex-emu-binfmt64
fi

# 2. The FEX rootfs and the Edge install inside it follow edge-fex exactly:
#    an Ubuntu 24.04 rootfs in $HOME/.local/share/fex-emu/RootFS, with the
#    amd64 Edge package installed inside. This script fetches the pinned
#    package. The rootfs build itself is the edge-fex procedure, and it is
#    the one part of setup that is still manual, because the rootfs tooling
#    wants a human watching the first boot.
cache_dir=$DATA_HOME/downloads
mkdir -p "$cache_dir"
if [ ! -f "$cache_dir/$EDGE_DEB" ]; then
  curl -fL "$EDGE_URL" -o "$cache_dir/$EDGE_DEB"
fi
echo "Edge package ready: $cache_dir/$EDGE_DEB"
echo "Install it inside the FEX rootfs per edge-fex, rootfs name Ubuntu_24_04_edge."

# 3. Native arm64 Chromium (Chrome for Testing build, pinned).
if [ ! -x "$HOME/chromium-arm64/chrome-linux-arm64/chrome" ]; then
  mkdir -p "$HOME/chromium-arm64"
  curl -fL "$CFT_URL" -o "$cache_dir/chrome-linux-arm64.zip"
  unzip -q "$cache_dir/chrome-linux-arm64.zip" -d "$HOME/chromium-arm64"
fi

# 4. Preferences that skip the first-run screens, and the sync config.
mkdir -p "$CONFIG_HOME" "$DATA_HOME"
cp "$(dirname "$0")/initial_preferences.json" "$DATA_HOME/initial_preferences.json"
if [ ! -f "$CONFIG_HOME/config.json" ]; then
  sed "s#/home/arduino#$HOME#g; s#/data/edge-arm-bridge#$PWD#g" "$(dirname "$0")/config.example.json" > "$CONFIG_HOME/config.json"
fi

# 5. Secret Service client for the keyring move. The service shells to
#    secret-tool rather than carrying a libsecret dependency.
if ! command -v secret-tool >/dev/null 2>&1; then
  echo "Install libsecret-tools so the service can move the Safe Storage secret: sudo apt-get install -y libsecret-tools" >&2
fi

# 6. Build the service.
dotnet build -c Release "$(dirname "$0")/../src/EdgeArmBridge"

echo
echo "Next, the one manual step: launch Edge under FEX with a fresh profile,"
echo "sign into the Microsoft account, let sync finish, and close Edge."
echo "Then run a first sync by hand:"
echo "  dotnet run --project src/EdgeArmBridge -- --once --config $CONFIG_HOME/config.json"
