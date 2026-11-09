#!/bin/sh
# Wake Edge under FEX long enough for Microsoft sync to pull the newest
# profile data, shut it down clean, then mirror the profile to Chromium.
# Run by the wake timer (see edge-arm-bridge.sh --service), or by hand.
# EDGE_ARM_BRIDGE_WAKE_SECONDS sets the dwell. Default 300.
set -u
BUNDLE=$(cd "$(dirname "$0")" && pwd)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}/edge-arm-bridge
EDGE=/opt/microsoft/msedge/msedge
WAKE_SECONDS=${EDGE_ARM_BRIDGE_WAKE_SECONDS:-300}
UNIT=edge-arm-bridge-wake

export FEX_ROOTFS="$BUNDLE/image/edge-image.squashfs"

if pgrep -f "user-data-dir=$DATA/edge-profile" >/dev/null 2>&1; then
  echo "wake: Edge is already running on this profile. Skipping the wake, going straight to the mirror."
else
  echo "wake: starting Edge headless for ${WAKE_SECONDS}s so sync can run"
  systemd-run --user --scope --quiet --unit="$UNIT" -p MemoryMax=6G -p MemorySwapMax=0 \
    "$BUNDLE/fex/usr/bin/FEX" "$EDGE" --browser-subprocess-path="$EDGE" \
    --no-sandbox --test-type --no-first-run --no-default-browser-check \
    --headless=new --user-data-dir="$DATA/edge-profile" about:blank &
  sleep "$WAKE_SECONDS"
  # A scope stop sends TERM first. Edge gets the chance to close the
  # profile clean, which the mirror's lock and WAL handling rely on.
  systemctl --user stop "$UNIT.scope" 2>/dev/null || true
  sleep 5
fi

echo "wake: mirroring to Chromium"
"$BUNDLE/sync.sh"
