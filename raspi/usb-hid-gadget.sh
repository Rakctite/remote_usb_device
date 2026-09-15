#!/bin/bash
set -euo pipefail

GADGET_ROOT=/sys/kernel/config/usb_gadget
GADGET_NAME=network-hid-bridge
GADGET="$GADGET_ROOT/$GADGET_NAME"

unbind_gadget() {
    if [[ -e "$GADGET/UDC" ]]; then
        printf '\n' > "$GADGET/UDC" || true
    fi
}

remove_gadget() {
    [[ -d "$GADGET" ]] || return 0
    unbind_gadget
    rm -f "$GADGET/configs/c.1/01-keyboard"
    rm -f "$GADGET/configs/c.1/02-mouse"
    rm -f "$GADGET/configs/c.1/03-consumer"
    rmdir "$GADGET/configs/c.1/strings/0x409" 2>/dev/null || true
    rmdir "$GADGET/configs/c.1" 2>/dev/null || true
    rmdir "$GADGET/functions/hid.keyboard" 2>/dev/null || true
    rmdir "$GADGET/functions/hid.mouse" 2>/dev/null || true
    rmdir "$GADGET/functions/hid.consumer" 2>/dev/null || true
    rmdir "$GADGET/strings/0x409" 2>/dev/null || true
    rmdir "$GADGET" 2>/dev/null || true
}

if [[ "${1:-start}" == stop ]]; then
    remove_gadget
    exit 0
fi
if [[ "${1:-start}" != start ]]; then
    echo "Usage: $0 {start|stop}" >&2
    exit 2
fi

getent group hidbridge >/dev/null || {
    echo "Required group 'hidbridge' does not exist; create it with the installer before starting the USB gadget." >&2
    exit 1
}

modprobe dwc2
modprobe libcomposite
mountpoint -q /sys/kernel/config || mount -t configfs configfs /sys/kernel/config

mapfile -t UDC_NAMES < <(find /sys/class/udc -mindepth 1 -maxdepth 1 -printf '%f\n' | sort)
if (( ${#UDC_NAMES[@]} == 0 )); then
    echo 'No USB Device Controller found. Check dtoverlay=dwc2,dr_mode=peripheral.' >&2
    exit 1
fi
if (( ${#UDC_NAMES[@]} > 1 )); then
    echo "Multiple UDCs found; choosing '${UDC_NAMES[0]}' deterministically from sorted names." >&2
fi
UDC_NAME=${UDC_NAMES[0]}

remove_gadget
trap 'remove_gadget' EXIT
mkdir -p "$GADGET"

printf '%s' '0x1d6b' > "$GADGET/idVendor"
printf '%s' '0x0104' > "$GADGET/idProduct"
printf '%s' '0x0106' > "$GADGET/bcdDevice"
printf '%s' '0x0200' > "$GADGET/bcdUSB"
printf '%s' '0x00' > "$GADGET/bDeviceClass"
printf '%s' '0x00' > "$GADGET/bDeviceSubClass"
printf '%s' '0x00' > "$GADGET/bDeviceProtocol"

mkdir -p "$GADGET/strings/0x409"
SERIAL=$(awk -F ': ' '/^Serial/ { print $2 }' /proc/cpuinfo | tail -n 1)
printf '%s' "${SERIAL:-0000000000000000}" > "$GADGET/strings/0x409/serialnumber"
printf '%s' 'IoTSys' > "$GADGET/strings/0x409/manufacturer"
printf '%s' 'Network HID Bridge' > "$GADGET/strings/0x409/product"

mkdir -p "$GADGET/configs/c.1/strings/0x409"
printf '%s' 'keyboard + mouse + consumer' > "$GADGET/configs/c.1/strings/0x409/configuration"
printf '%s' '0xC0' > "$GADGET/configs/c.1/bmAttributes"
printf '%s' '1' > "$GADGET/configs/c.1/MaxPower"

# Boot keyboard: modifiers, reserved byte, six key usages, and five LED outputs.
mkdir -p "$GADGET/functions/hid.keyboard"
printf '%s' '1' > "$GADGET/functions/hid.keyboard/protocol"
printf '%s' '1' > "$GADGET/functions/hid.keyboard/subclass"
printf '%s' '8' > "$GADGET/functions/hid.keyboard/report_length"
printf '%b' '\x05\x01\x09\x06\xA1\x01\x05\x07\x19\xE0\x29\xE7\x15\x00\x25\x01\x75\x01\x95\x08\x81\x02\x95\x01\x75\x08\x81\x01\x95\x06\x75\x08\x15\x00\x26\xA4\x00\x05\x07\x19\x00\x2A\xA4\x00\x81\x00\x95\x05\x75\x01\x05\x08\x19\x01\x29\x05\x91\x02\x95\x01\x75\x03\x91\x01\xC0' > "$GADGET/functions/hid.keyboard/report_desc"

# Five-button mouse with X/Y movement and vertical wheel.
mkdir -p "$GADGET/functions/hid.mouse"
printf '%s' '2' > "$GADGET/functions/hid.mouse/protocol"
printf '%s' '1' > "$GADGET/functions/hid.mouse/subclass"
printf '%s' '4' > "$GADGET/functions/hid.mouse/report_length"
printf '%b' '\x05\x01\x09\x02\xA1\x01\x09\x01\xA1\x00\x05\x09\x19\x01\x29\x05\x15\x00\x25\x01\x95\x05\x75\x01\x81\x02\x95\x01\x75\x03\x81\x01\x05\x01\x09\x30\x09\x31\x09\x38\x15\x81\x25\x7F\x75\x08\x95\x03\x81\x06\xC0\xC0' > "$GADGET/functions/hid.mouse/report_desc"

# Consumer control: one unsigned 16-bit usage ID plus signed relative AC Pan.
mkdir -p "$GADGET/functions/hid.consumer"
printf '%s' '0' > "$GADGET/functions/hid.consumer/protocol"
printf '%s' '0' > "$GADGET/functions/hid.consumer/subclass"
printf '%s' '3' > "$GADGET/functions/hid.consumer/report_length"
printf '%b' '\x05\x0C\x09\x01\xA1\x01\x15\x00\x26\xFF\x03\x19\x00\x2A\xFF\x03\x75\x10\x95\x01\x81\x00\x09\x38\x15\x81\x25\x7F\x75\x08\x95\x01\x81\x06\xC0' > "$GADGET/functions/hid.consumer/report_desc"

ln -s "$GADGET/functions/hid.keyboard" "$GADGET/configs/c.1/01-keyboard"
ln -s "$GADGET/functions/hid.mouse" "$GADGET/configs/c.1/02-mouse"
ln -s "$GADGET/functions/hid.consumer" "$GADGET/configs/c.1/03-consumer"
printf '%s' "$UDC_NAME" > "$GADGET/UDC"

expected_nodes=(/dev/hidg0 /dev/hidg1 /dev/hidg2)
max_attempts=50
nodes_ready=false
for ((attempt = 1; attempt <= max_attempts; attempt++)); do
    nodes_ready=true
    for node in "${expected_nodes[@]}"; do
        [[ -c "$node" ]] || nodes_ready=false
    done
    if [[ -e /dev/hidg3 ]]; then
        echo 'Unexpected HID node /dev/hidg3 appeared; expected exactly /dev/hidg0 through /dev/hidg2.' >&2
        exit 1
    fi
    [[ "$nodes_ready" == true ]] && break
    sleep 0.1
done

if [[ "$nodes_ready" != true ]]; then
    echo 'Timed out waiting for character devices /dev/hidg0, /dev/hidg1, and /dev/hidg2.' >&2
    exit 1
fi

for node in "${expected_nodes[@]}"; do
    [[ -c "$node" ]] || {
        echo "Expected $node to be a character device." >&2
        exit 1
    }
    chgrp hidbridge "$node"
    chmod 0660 "$node"
done

trap - EXIT
