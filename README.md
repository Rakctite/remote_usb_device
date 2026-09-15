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
