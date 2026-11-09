# Shared setup for the edge-arm-bridge command wrappers (RPM install).
# The payload lives in /usr/lib/edge-arm-bridge; profiles, config, and work
# files live in the user's home, created on first use.
EAB_ROOT="/usr/lib/edge-arm-bridge"
EAB_DATA="$HOME/.local/share/edge-arm-bridge"
EAB_CONFIG="$HOME/.config/edge-arm-bridge/config.json"

edge_arm_bridge_ensure_config() {
    mkdir -p "$EAB_DATA/work" "$EAB_DATA/backups" "$(dirname "$EAB_CONFIG")"
    if [ ! -f "$EAB_CONFIG" ]; then
        local chromium_bin="$EAB_ROOT/chromium/chrome"
        if [ -x /usr/bin/google-chrome ]; then chromium_bin="/usr/bin/google-chrome"
        elif [ -x /usr/bin/chromium ]; then chromium_bin="/usr/bin/chromium"; fi
        printf '{\n  "EdgeProfilePath": "%s",\n  "ChromiumProfilePath": "%s",\n  "ChromiumBinaryPath": "%s",\n  "WorkDir": "%s"\n}\n' \
            "$EAB_DATA/edge-profile" "$EAB_DATA/chromium-profile" "$chromium_bin" "$EAB_DATA/work" > "$EAB_CONFIG"
    fi
}
