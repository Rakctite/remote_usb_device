# Network HID Bridge

This repository contains two cooperating components:

- a .NET 9 Windows desktop application under `src/windows`; and
- Raspberry Pi USB gadget setup plus Python 3.11+ protocol/authentication code.

The Windows application is ready for desktop-side testing. The Raspberry Pi network receiver/service is still pending; its USB gadget setup and protocol/authentication primitives are present.

## Debug the Windows application

1. Open `NetworkHidBridge.sln` in Visual Studio 2022.
2. Set `NetworkHidBridge.App` as the startup project.
3. Select `Debug` and `x64`, then press F5.
4. Accept the Windows administrator prompt. Administrator elevation is required for reliable global input capture and suppression.

The initial endpoint is `192.168.0.4:45821`. Settings are saved under `%LocalAppData%\IoTSys\NetworkHidBridge\settings.json`; the 32-byte PSK is protected for the current Windows user with DPAPI. The same PSK must later be installed on the Pi receiver.

Routing starts in **Default** mode (main PC enabled, remote PC disabled). Press numpad `-` and numpad `*` together to switch between Default and Remote-only. Remote modes remain unavailable until the Pi connection is authenticated. Closing the window keeps the tray application running; use the tray's Exit command to stop it.

## Build and test

Install the Python test tooling, then run from the repository root:

```powershell
python -m pip install -e ".[test]"
dotnet build NetworkHidBridge.sln
dotnet test NetworkHidBridge.sln
python -m pytest tests/pi -q
```
