# Network HID Bridge

This repository contains two cooperating components:

- a .NET 9 Windows desktop application under `src/windows`; and
- a Raspberry Pi Python 3.11+ receiver and three-interface USB HID Gadget service.

Both applications are implemented. The Pi installer creates a dedicated service account, installs the receiver without downloading runtime packages, protects its configuration, and enables both systemd services.

## Debug the Windows application

1. Open `NetworkHidBridge.sln` in Visual Studio 2022.
2. Set `NetworkHidBridge.App` as the startup project.
3. Select `Debug` and `x64`, then press F5.
4. Accept the Windows administrator prompt. Administrator elevation is required for reliable global input capture and suppression.

The initial endpoint is `192.168.0.4:45821`. Settings are saved under `%LocalAppData%\IoTSys\NetworkHidBridge\settings.json`; the 32-byte PSK is protected for the current Windows user with DPAPI. The same PSK must later be installed on the Pi receiver.

Routing starts in **Default** mode (main PC enabled, remote PC disabled). Press numpad `-` and numpad `*` together to switch between Default and Remote-only. Remote modes remain unavailable until the Pi connection is authenticated. Closing the window keeps the tray application running; use the tray's Exit command to stop it.

## Install the Raspberry Pi receiver

The Pi must already expose its SPI Ethernet interface at the address used by the Windows application. Before assigning `192.168.0.4`, inspect the actual interface name and network manager on the installed Raspberry Pi OS image:

```bash
ip -br link
ip -br address
nmcli general status 2>/dev/null || true
systemctl is-active systemd-networkd
```

Configure the static address using whichever manager owns that interface. The installer intentionally does not modify Ethernet, Wi-Fi, Bluetooth, boot overlays, or Raspberry Pi OS network configuration. Only use `192.168.0.4` after that address appears on the intended wired interface.

Copy `src/pi` and `raspi` to the Pi, then run from their common parent directory:

```bash
sudo bash raspi/install.sh --listen-address 192.168.0.4 --port 45821
```

Paste the Base64 PSK shown in the Windows application's settings window when prompted. Input is hidden. For unattended provisioning, `--psk BASE64` is also supported, but it can remain in shell history and process inspection, so interactive input is preferred.

The resulting configuration is `/etc/network-hid-bridge/config.json`, owned by `root:hidbridge` with mode `0640`. Application source is installed at `/opt/network-hid-bridge`. Check operation with:

```bash
sudo systemctl status usb-hid-gadget.service network-hid-bridge.service
sudo journalctl -u network-hid-bridge.service -n 100 --no-pager
ls -l /dev/hidg0 /dev/hidg1 /dev/hidg2
```

## Build and test

Install the Python test tooling, then run from the repository root:

```powershell
python -m pip install -e ".[test]"
dotnet build NetworkHidBridge.sln
dotnet test NetworkHidBridge.sln
python -m pytest tests/pi -q
```

## Linux amd64 sender

The Linux sender shares the original TCP protocol and Raspberry Pi receiver with
the Windows app. Build the Linux project independently of the Windows solution:

```sh
dotnet publish src/linux/NetworkHidBridge.Linux -c Release -r linux-x64 --self-contained true
```

Run the published NetworkHidBridge.Linux executable from an active graphical
logind session. On Ubuntu 24.04, the sender needs libevdev2 and Avalonia X11 dependencies (including libice6, libsm6, and libfontconfig1). Install libsecret-tools for desktop keyring support.
The desktop keyring is optional if you enter the pre-shared key each session.
Select the keyboard and relative mouse event devices listed by their stable udev
paths. The current user needs read access to those device nodes via the desktop
session or a local udev ACL scoped to the intended devices.

Enter the Pi IP, port and Base64 PSK from the Pi setup, then select Connect.
Wait for Connected before selecting a routing mode. Numpad subtract and multiply
toggle remote routing after keys and buttons are released. In Default mode,
the local app can receive those two keys. Closing the window exits the sender.
After unplugging a selected device, reconnect it and reopen the sender if the
device list needs to be refreshed.

This version selects one event node for each role. If a keyboard exposes media
keys on a separate event node, those keys are available only when they are
reported through the selected keyboard node.
