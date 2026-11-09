#!/bin/bash
# edge-arm-bridge: install, configure, and run the Edge to Chromium mirror.
#
#   curl -fsSL https://github.com/sirredbeard/edge-arm-bridge/releases/latest/download/edge-arm-bridge.sh | bash -s -- --install
#   ~/.local/opt/edge-arm-bridge/edge-arm-bridge.sh --configure
#   ~/.local/opt/edge-arm-bridge/edge-arm-bridge.sh --service
#
# --install    Download the newest release bundle, untar it into
#              ~/.local/opt/edge-arm-bridge, and set it up. Nothing is
#              installed system-wide and no package manager is touched.
# --configure  Open the bundled Edge under FEX for the one manual step:
#              sign into the Microsoft account and let sync finish.
# --service    Install and enable the systemd user timer that wakes Edge
#              every 6 hours, lets it sync, and mirrors the profile to
#              Chromium.
# --sync       Run one mirror now. --wake runs one wake-and-mirror now.
#
# EDGE_ARM_BRIDGE_HOME overrides the install directory.
# EDGE_ARM_BRIDGE_TARBALL points --install at a local bundle file instead
# of the newest GitHub release (used by the project's own tests).
set -euo pipefail
APP_HOME=${EDGE_ARM_BRIDGE_HOME:-$HOME/.local/opt/edge-arm-bridge}
REPO=sirredbeard/edge-arm-bridge

usage() { sed -n '2,20p' "$0" 2>/dev/null || echo "edge-arm-bridge.sh --install | --configure | --service | --sync | --wake"; }

require_install() {
  if [ ! -x "$APP_HOME/run-edge.sh" ]; then
    echo "edge-arm-bridge is not installed at $APP_HOME yet. Run with --install first." >&2
    exit 1
  fi
}

cmd_install() {
  local tmp tarball sha_url api
  tmp=$(mktemp -d)
  trap "rm -rf $tmp" EXIT
  if [ -n "${EDGE_ARM_BRIDGE_TARBALL:-}" ]; then
    tarball=$EDGE_ARM_BRIDGE_TARBALL
    echo "Installing from local bundle: $tarball"
  else
    api=https://api.github.com/repos/$REPO/releases/latest
    echo "Finding the newest release of $REPO"
    tarball=$tmp/edge-arm-bridge.tar.gz
    local url
    url=$(curl -fsSL "$api" | grep -oE '"browser_download_url": "[^"]+edge-arm-bridge\.tar\.gz"' | head -1 | cut -d'"' -f4)
    sha_url=$(curl -fsSL "$api" | grep -oE '"browser_download_url": "[^"]+edge-arm-bridge\.tar\.gz\.sha256"' | head -1 | cut -d'"' -f4)
    if [ -z "$url" ]; then
      echo "No bundle asset found on the newest release of $REPO." >&2
      exit 1
    fi
    echo "Downloading $url"
    curl -fL "$url" -o "$tarball"
    if [ -n "$sha_url" ]; then
      curl -fsSL "$sha_url" -o "$tmp/edge-arm-bridge.tar.gz.sha256"
      (cd "$tmp" && sha256sum -c edge-arm-bridge.tar.gz.sha256)
    fi
  fi
  rm -rf "$APP_HOME.new"
  mkdir -p "$APP_HOME.new"
  tar -xzf "$tarball" -C "$APP_HOME.new" --strip-components=1
  rm -rf "$APP_HOME.old"
  if [ -e "$APP_HOME" ]; then mv "$APP_HOME" "$APP_HOME.old"; fi
  mv "$APP_HOME.new" "$APP_HOME"
  rm -rf "$APP_HOME.old"
  chmod +x "$APP_HOME"/*.sh "$APP_HOME/bridge/EdgeArmBridge" 2>/dev/null || true
  EDGE_ARM_BRIDGE_HOME=$APP_HOME "$APP_HOME/install.sh"
  echo
  echo "Installed at $APP_HOME"
  echo "Next: $APP_HOME/edge-arm-bridge.sh --configure"
}

cmd_configure() {
  require_install
  if [ -z "${WAYLAND_DISPLAY:-}" ] && [ -z "${DISPLAY:-}" ]; then
    echo "--configure opens Edge in a window for the Microsoft sign-in, and" >&2
    echo "there is no display in this session. Run it from your desktop" >&2
    echo "session instead:" >&2
    echo "  $APP_HOME/edge-arm-bridge.sh --configure" >&2
    exit 1
  fi
  echo "Opening Edge. Sign into the Microsoft account, let sync finish,"
  echo "then close the Edge window."
  "$APP_HOME/run-edge.sh"
  echo
  echo "Profile is on disk. Next: $APP_HOME/edge-arm-bridge.sh --service"
  echo "or mirror once by hand: $APP_HOME/edge-arm-bridge.sh --sync"
}

cmd_service() {
  require_install
  local unit_dir=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user
  mkdir -p "$unit_dir"
  sed "s#@BUNDLE@#$APP_HOME#g" "$APP_HOME/systemd/edge-arm-bridge-wake.service" > "$unit_dir/edge-arm-bridge-wake.service"
  cp "$APP_HOME/systemd/edge-arm-bridge-wake.timer" "$unit_dir/edge-arm-bridge-wake.timer"
  if systemctl --user daemon-reload 2>/dev/null && systemctl --user enable --now edge-arm-bridge-wake.timer 2>/dev/null; then
    echo "Service installed. The wake timer fires at 00:15, 06:15, 12:15, and 18:15,"
    echo "wakes Edge for 5 minutes to sync, then mirrors the profile to Chromium."
    echo "Check it with: systemctl --user list-timers edge-arm-bridge-wake.timer"
  else
    echo "Units written to $unit_dir, but systemctl --user could not be" >&2
    echo "reached from this session. From your desktop session, run:" >&2
    echo "  systemctl --user daemon-reload" >&2
    echo "  systemctl --user enable --now edge-arm-bridge-wake.timer" >&2
  fi
}

case "${1:-}" in
  --install) cmd_install ;;
  --configure) cmd_configure ;;
  --service) cmd_service ;;
  --sync) require_install; exec "$APP_HOME/sync.sh" ;;
  --wake) require_install; exec "$APP_HOME/wake-and-sync.sh" ;;
  *) usage; exit 1 ;;
esac
