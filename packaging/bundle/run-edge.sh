#!/bin/sh
# Launch the bundled x86-64 Edge under the bundled self-contained FEX, for
# sign-in. FEX carries it's own loader and libraries (interpreter patched
# by install.sh), and the Edge image is the squashfs in image/. Same launch
# rules as edge-fex: the child-process path, --no-sandbox under FEX,
# Wayland when the session offers it. Extra arguments pass through.
set -eu
BUNDLE=$(cd "$(dirname "$0")" && pwd)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}/edge-arm-bridge
EDGE=/opt/microsoft/msedge/msedge
if [ -n "${WAYLAND_DISPLAY:-}" ]; then
  set -- --ozone-platform=wayland "$@"
fi
export FEX_ROOTFS="$BUNDLE/image/edge-image.squashfs"
exec systemd-run --user --scope --quiet -p MemoryMax=6G -p MemorySwapMax=0 \
  "$BUNDLE/fex/usr/bin/FEX" "$EDGE" --browser-subprocess-path="$EDGE" \
  --no-sandbox --test-type --no-first-run --no-default-browser-check \
  --user-data-dir="$DATA/edge-profile" "$@"
