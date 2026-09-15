#!/bin/bash
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
APP_DIR=/opt/network-hid-bridge
CONFIG_DIR=/etc/network-hid-bridge
CONFIG_PATH=$CONFIG_DIR/config.json
LISTEN_ADDRESS=192.168.0.4
PORT=45821
PSK=

usage() {
    echo "Usage: sudo $0 [--listen-address IP] [--port PORT] [--psk BASE64]" >&2
}

while (( $# > 0 )); do
    case "$1" in
        --listen-address)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            LISTEN_ADDRESS=$2
            shift 2
            ;;
        --port)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            PORT=$2
            shift 2
            ;;
        --psk)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            PSK=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage
            exit 2
            ;;
    esac
done

if (( EUID != 0 )); then
    echo "Run this installer as root." >&2
    exit 1
fi

if [[ -z "$PSK" ]]; then
    read -r -s -p "PSK (32-byte Base64 value from the Windows app): " PSK
    echo
fi
if [[ -z "$PSK" ]]; then
    echo "PSK must not be empty." >&2
    exit 1
fi

for required in \
    "$PROJECT_ROOT/src/pi/network_hid_bridge/__main__.py" \
    "$PROJECT_ROOT/raspi/usb-hid-gadget.sh" \
    "$PROJECT_ROOT/raspi/usb-hid-gadget.service" \
    "$PROJECT_ROOT/raspi/network-hid-bridge.service"; do
    [[ -f "$required" ]] || { echo "Missing installation asset: $required" >&2; exit 1; }
done

getent group hidbridge >/dev/null || groupadd --system hidbridge
if ! id -u hidbridge >/dev/null 2>&1; then
    useradd --system --gid hidbridge --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin hidbridge
fi
usermod --gid hidbridge --home /nonexistent --shell /usr/sbin/nologin hidbridge

install -d -m 0755 -o root -g root "$APP_DIR" "$APP_DIR/network_hid_bridge"
for source in "$PROJECT_ROOT"/src/pi/network_hid_bridge/*.py; do
    install -m 0644 -o root -g root "$source" "$APP_DIR/network_hid_bridge/$(basename "$source")"
done

install -d -m 0750 -o root -g hidbridge "$CONFIG_DIR"
CONFIG_TEMP=$(mktemp "$CONFIG_DIR/.config.json.XXXXXX")
cleanup() {
    rm -f -- "$CONFIG_TEMP"
}
trap cleanup EXIT

printf '%s\0%s\0%s' "$LISTEN_ADDRESS" "$PORT" "$PSK" | python3 -c '
import base64
import binascii
import ipaddress
import json
from pathlib import Path
import sys

parts = sys.stdin.buffer.read().split(b"\0")
if len(parts) != 3:
    raise SystemExit("invalid installer input")
address_text, port_text, psk_text = (part.decode("utf-8") for part in parts)
try:
    address = ipaddress.ip_address(address_text)
    port = int(port_text)
    psk = base64.b64decode(psk_text, validate=True)
except (ValueError, UnicodeError, binascii.Error) as error:
    raise SystemExit("invalid address, port, or PSK") from error
if address.is_unspecified or address.is_multicast:
    raise SystemExit("listen address must be a specific unicast address")
if not 1 <= port <= 65535:
    raise SystemExit("port must be between 1 and 65535")
if len(psk) != 32:
    raise SystemExit("PSK must decode to exactly 32 bytes")
config = {
    "listen_address": str(address),
    "port": port,
    "psk": psk_text,
    "keyboard_path": "/dev/hidg0",
    "mouse_path": "/dev/hidg1",
    "consumer_path": "/dev/hidg2",
}
Path(sys.argv[1]).write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
' "$CONFIG_TEMP"
chmod 0640 "$CONFIG_TEMP"
chown root:hidbridge "$CONFIG_TEMP"
mv -f "$CONFIG_TEMP" "$CONFIG_PATH"
trap - EXIT

install -m 0755 -o root -g root "$PROJECT_ROOT/raspi/usb-hid-gadget.sh" /usr/local/sbin/usb-hid-gadget
install -m 0644 -o root -g root "$PROJECT_ROOT/raspi/usb-hid-gadget.service" /etc/systemd/system/usb-hid-gadget.service
install -m 0644 -o root -g root "$PROJECT_ROOT/raspi/network-hid-bridge.service" /etc/systemd/system/network-hid-bridge.service

systemctl daemon-reload
systemctl enable usb-hid-gadget.service network-hid-bridge.service
systemctl stop network-hid-bridge.service 2>/dev/null || true
systemctl restart usb-hid-gadget.service
systemctl start network-hid-bridge.service
echo "Network HID Bridge installed for $LISTEN_ADDRESS:$PORT."
