#!/bin/sh
# Install edge-arm-bridge from the untarred bundle. No package manager is
# involved, and nothing is installed system-wide. The script untars into
# place, points the bundled FEX binaries at the bundled loader, and writes
# per-user files. Run it as your normal user.
set -eu
cd "$(dirname "$0")"
BUNDLE=$PWD
DATA=${XDG_DATA_HOME:-$HOME/.local/share}/edge-arm-bridge
CONFIG_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/edge-arm-bridge

if [ "$(uname -m)" != "aarch64" ]; then
  echo "This bundle is for arm64 hosts. Got: $(uname -m)" >&2
  exit 1
fi

# Self-contained FEX: the binaries ship with an rpath that finds the bundled
# libraries relative to the binary ($ORIGIN), but a PT_INTERP interpreter
# path is absolute and cannot be. Patch it here, where the real bundle path
# is known, using the bundled loader to run the bundled patchelf. After this
# step FEX needs nothing from the host C library at all.
LOADER="$BUNDLE/fex/usr/lib/aarch64-linux-gnu/ld-linux-aarch64.so.1"
for bin in FEX FEXServer; do
  "$LOADER" "$BUNDLE/fex/usr/bin/patchelf" --set-interpreter "$LOADER" "$BUNDLE/fex/usr/bin/$bin"
done
echo "FEX interpreter pointed at the bundled loader."

# The Edge image is a squashfs file. FEX mounts it through FUSE, so the host
# needs /dev/fuse. Desktops have it. Say so plainly where it is missing
# rather than letting the first sign-in run fail stranger.
if [ ! -e /dev/fuse ]; then
  echo "Warning: /dev/fuse is not present, and FEX needs it to mount the" >&2
  echo "Edge image (image/edge-image.squashfs). The bundle also builds in a" >&2
  echo "loose-directory form: see packaging/build-bundle.sh." >&2
fi

# Chromium still links host libraries (libstdc++, the graphics stack). A
# desktop install has them. Where they are missing, name them and stop
# rather than guessing at a package manager.
CHROME="$BUNDLE/chromium/chrome-linux-arm64/chrome"
if [ -x "$CHROME" ]; then
  missing=$(ldd "$CHROME" 2>/dev/null | awk '/not found/ {print $1}')
  if [ -n "$missing" ]; then
    echo "Host libraries missing for the bundled Chromium:" >&2
    for lib in $missing; do echo "  $lib" >&2; done
    echo "Install your distribution's standard desktop libraries for these, then run install.sh again." >&2
    exit 1
  fi
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
echo "  3. Periodic wake + sync: $BUNDLE/edge-arm-bridge.sh --service"
