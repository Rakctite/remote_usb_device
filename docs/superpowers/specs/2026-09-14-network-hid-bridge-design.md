# Network HID Bridge Design

## Goal

Build a two-part system that captures physical keyboard and mouse input on a
Windows 10 or Windows 11 computer, transports selected input over a trusted
wired LAN, and presents that input to a separate target computer as physical
USB HID devices through a Raspberry Pi Zero 2 W.

The target computer must see a USB composite device containing distinct
keyboard, mouse, and consumer-control interfaces. Basic keyboard and mouse
operation must remain usable in BIOS/UEFI. Extended controls are intended for
the target operating system.

## Hardware

- Raspberry Pi Zero 2 W with a populated 40-pin header
- GPIO/SPI Ethernet adapter, with an ENC28J60 HAT as the initial target
- Dedicated 5 V / 2.5 A supply connected to `PWR IN`
- The port labelled `USB` configured as the USB Device/Gadget connection
- A data connection to the target computer with target VBUS isolated

The GPIO Ethernet adapter must not consume the Zero 2 W USB OTG controller.
The target USB cable keeps D+, D-, and GND connected. Its 5 V VBUS conductor is
disconnected when the Pi is powered through `PWR IN`. A suitable power/data
splitter may provide the same isolation without modifying a cable.

## System Architecture

### Windows application

The Windows component is a C# .NET 9 WPF tray application. It runs elevated
and contains these independently testable components:

- `InputCapture`: low-level keyboard hooks and Raw Input mouse collection
- `InputState`: pressed-key, mouse-button, and ownership state
- `ModeController`: routing policy and mode-hotkey handling
- `Protocol`: binary frame encoding, sequencing, and authentication
- `NetworkClient`: connection, heartbeat, and automatic reconnection
- `Settings`: endpoint, PSK, startup, and local persistence
- `Tray UI`: connection status, mode selection, settings, and explicit exit

Raw Input provides relative mouse movement even while the low-level mouse hook
suppresses local cursor activity. Injected Windows events are ignored so the
application does not recapture synthetic input.

### Raspberry Pi services

The Pi contains two systemd services:

- `usb-hid-gadget.service` creates and binds the composite USB Gadget.
- `network-hid-bridge.service` runs the Python network receiver and writes HID
  reports.

The Python 3 receiver is installed under `/opt/network-hid-bridge`. It uses the
standard library where practical and is divided into protocol, authentication,
input-state, and HID-output modules.

The Gadget setup runs as root. The receiver runs as a dedicated `hidbridge`
user that can only write the `/dev/hidg*` nodes through group permissions.

## USB HID Layout

The USB composite device contains three interfaces:

1. Boot-compatible keyboard
2. Extended relative mouse
3. Consumer Control

The keyboard supports modifiers, navigation keys, the number pad, and F1-F24.
It uses six-key rollover for non-modifier keys so its boot-keyboard behavior
remains conventional. The extended mouse supports five buttons, relative X/Y,
vertical wheel, and horizontal pan. Consumer Control supports volume, mute,
play/pause, stop, previous track, and next track.

BIOS/UEFI support is required for the basic keyboard and basic pointer
functions. Consumer controls, F13-F24, extra mouse buttons, and horizontal pan
are only guaranteed in a full target operating system.

The initial development setup currently enumerates keyboard and mouse as
`/dev/hidg0` and `/dev/hidg1`. Implementation will add Consumer Control as the
third stable interface and will verify the final node-to-interface mapping at
service startup instead of relying silently on accidental ordering.

## Input Modes

The application provides four routing modes:

| Mode | Main Windows PC | Remote target PC |
| --- | --- | --- |
| Default | Allowed | Blocked |
| Remote only | Blocked | Allowed |
| Shared | Allowed | Allowed |
| Block all | Blocked | Blocked |

The initial mode is always Default.

The reserved mode hotkey is simultaneous Number Pad Subtract and Number Pad
Multiply. These two keys are consumed by the application and are never routed
to either computer. In Default mode, the hotkey enters Remote only. In any
other mode, it returns to Default. Shared and Block all remain selectable from
the tray UI and settings window.

When local routing is disabled, a key or button whose press was already passed
to Windows retains local ownership until its corresponding release is passed.
This prevents a locally stuck key. When remote routing is disabled, the client
sends Release All. When remote routing becomes enabled, keys and buttons that
were already held are ignored until physically released and pressed again.

## Network Protocol

The Windows client connects to a manually configured IP address and TCP port.
Defaults are `192.168.0.4` and `45821`. The socket uses `TCP_NODELAY`.

The binary protocol has a fixed header containing a magic value, protocol
version, message type, sequence number, and payload length. Authentication uses
a pre-shared key, independent client and server nonces, and HMAC-SHA256. A
session key is derived from the PSK and both nonces. Every post-authentication
frame carries a truncated authentication tag, and monotonically increasing
sequence numbers reject replay or reordering. The protocol authenticates and
protects integrity but does not conceal input contents on the trusted LAN.

Message types are limited to:

- authentication handshake
- keyboard state
- mouse delta and button state
- consumer-control state
- complete state synchronization
- Release All
- heartbeat and heartbeat acknowledgement

Keyboard and consumer state is sent on every change and periodically as a full
snapshot. Mouse deltas are collected and transmitted at no more than 125 Hz.
The Pi splits large relative movement into valid HID-sized reports without
discarding accumulated motion.

Only one authenticated client may control the Pi at a time. A new session is
accepted only after the previous session has closed or timed out.

## Failure Handling

- A Windows socket failure immediately selects Default mode.
- Closing or crashing the Windows process removes its user-mode hooks, so local
  input returns without a separate recovery process.
- Disabling remote routing sends Release All before remote state is discarded.
- The Pi sends empty keyboard, mouse, and consumer reports when TCP closes,
  authentication fails after input began, the receiver stops, or heartbeat is
  absent for one second.
- Reconnection begins with empty input state; held physical controls must be
  released before they can be routed remotely.
- Malformed, oversized, incorrectly sequenced, or incorrectly authenticated
  frames terminate the connection without logging input contents.

User-mode Windows hooks cannot control the secure attention sequence,
Ctrl+Alt+Delete, the UAC secure desktop, or input handled outside the interactive
desktop. These cases are explicitly outside the first version's scope.

## Windows User Experience

Closing the WPF window hides it to the notification area. Only the tray Exit
command terminates the process. The tray menu provides the four modes,
reconnect, settings, and exit. The icon and tooltip display current connection
and routing state.

The settings window provides:

- Pi IP address and TCP port
- pre-shared key
- automatic connection and reconnection status
- round-trip latency
- four mode controls
- Windows-login startup setting
- diagnostic log viewer

Settings are stored below `%LocalAppData%`. The PSK is encrypted using Windows
DPAPI. Automatic elevated startup uses a Task Scheduler entry configured to run
with highest privileges rather than an ordinary startup-folder shortcut.

Remote only and Shared cannot be selected without an authenticated Pi
connection. Such an attempt leaves the application in Default and displays a
clear status notification.

## Pi Configuration and Operations

The receiver configuration is stored under
`/etc/network-hid-bridge/config.json`; secrets are readable only by the service
account and administrator. The listener binds to the configured address and
port rather than all interfaces by default.

Both services start automatically. The receiver starts only after the Gadget
service has successfully created all expected HID nodes. Logs go to journald
and include lifecycle, connection, authentication result, protocol error, and
watchdog events. Logs never contain keys, text, mouse movement, or PSK values.

The built-in Wi-Fi and Bluetooth radios may be disabled during deployment once
the SPI Ethernet interface is proven stable, but radio disablement is not
required for development.

## Testing

### Automated Windows tests

- mode-transition truth table
- Number Pad Subtract/Multiply hotkey consumption
- local ownership and delayed release behavior
- remote held-key suppression after mode entry
- Windows scan-code to HID usage mapping
- mouse delta accumulation
- packet encoding, authentication tags, and sequence handling
- disconnect fallback to Default

### Automated Python tests

- handshake acceptance and rejection
- frame parsing, size limits, tags, and sequence validation
- keyboard, mouse, and consumer-state handling
- large mouse-delta splitting
- Release All on every termination path
- single-client ownership
- heartbeat timeout
- HID output against temporary fake device files

### Integration and hardware tests

- C# client to Python server loopback with fake HID files
- Windows enumeration of all three HID interfaces
- normal keys, modifiers, F1-F24, and number pad
- five mouse buttons, X/Y, vertical wheel, and horizontal pan
- all defined consumer-control keys
- all four routing modes
- hotkey transition from Default and every non-default mode
- cable removal, network loss, client crash, and Pi daemon restart
- Pi reboot and automatic service recovery
- manual BIOS/UEFI keyboard and pointer verification

## Delivery Boundaries

The first version targets Windows 10/11 and Raspberry Pi OS on Zero 2 W. It
does not include video capture, clipboard transfer, file transfer, audio,
Internet traversal, multi-client arbitration, a signed kernel filter driver,
or control of the Windows secure desktop.
