#!/bin/sh
# Install edge-arm-bridge from the untarred bundle. No package manager is
# involved: everything the bundle needs is inside the bundle, and this
# script only writes per-user files. Run it as your normal user.
set -eu
cd "$(dirname "$0")"
BUNDLE=$PWD
DATA=${XDG_DATA_HOME:-$HOME/.local/share}/edge-arm-bridge
CONFIG_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/edge-arm-bridge

if [ "$(uname -m)" != "aarch64" ]; then
  echo "This bundle is for arm64 hosts. Got: $(uname -m)" >&2
  exit 1
fi

# FEX and Chromium link a short list of host libraries (libstdc++, libGL,
# and friends). A desktop install has them. A bare server may not, and the
# honest answer there is to say which ones are missing, not to guess at a
# package manager.
missing=""
for bin in "$BUNDLE/fex/usr/bin/FEX" "$BUNDLE/chromium/chrome-linux-arm64/chrome"; do
  for lib in $(ldd "$bin" 2>/dev/null | awk '/not found/ {print $1}'); do
    missing="$missing $lib"
  done
done
if [ -n "$missing" ]; then
  echo "Host libraries missing for the bundled binaries:" >&2
  for lib in $missing; do echo "  $lib" >&2; done
  echo "Install your distribution's standard desktop libraries for these, then run install.sh again." >&2
  exit 1
fi

mkdir -p "$DATA/edge-profile" "$DATA/chromium-profile" "$DATA/work" "$CONFIG_DIR"
if [ ! -f "$CONFIG_DIR/config.json" ]; then
  sed -e "s#@BUNDLE@#$BUNDLE#g" -e "s#@DATA@#$DATA#g" -e "s#@HOME@#$HOME#g" \
    "$BUNDLE/config.template.json" > "$CONFIG_DIR/config.json"
fi
chmod +x "$BUNDLE/run-edge.sh" "$BUNDLE/sync.sh" "$BUNDLE/bridge/EdgeArmBridge" 2>/dev/null || true

# User units, with the bundle path filled in. Installed, not enabled: the
# first mirror should be watched, per the README.
UNIT_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user
mkdir -p "$UNIT_DIR"
sed "s#@BUNDLE@#$BUNDLE#g" "$BUNDLE/systemd/edge-arm-bridge.service" > "$UNIT_DIR/edge-arm-bridge.service"
cp "$BUNDLE/systemd/edge-arm-bridge.timer" "$UNIT_DIR/edge-arm-bridge.timer"

echo "Installed. Config: $CONFIG_DIR/config.json"
echo

echo "Next steps:"
echo "  1. Sign in, once:        $BUNDLE/run-edge.sh"
echo "     Sign into the Microsoft account in the Edge window, let sync"
echo "     finish, then close Edge."
echo "  2. First mirror by hand: $BUNDLE/sync.sh"
echo "  3. Daily mirror:         systemctl --user enable --now edge-arm-bridge.timer"
