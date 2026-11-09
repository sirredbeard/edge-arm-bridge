#!/bin/sh
# Run one profile mirror with the bundled service.
set -eu
BUNDLE=$(cd "$(dirname "$0")" && pwd)
CONFIG_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/edge-arm-bridge
exec "$BUNDLE/bridge/EdgeArmBridge" --once --config "$CONFIG_DIR/config.json"
