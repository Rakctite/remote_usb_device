# Network HID Bridge

This repository contains the early skeleton for two cooperating components:

- a .NET 9 Windows desktop application under `src/windows`; and
- a Python 3.11+ Raspberry Pi receiver package under `src/pi`.

The transport, input-routing, authentication, and USB HID behavior described in the design documents are not implemented yet.

Run the current smoke tests from the repository root:

```powershell
dotnet test NetworkHidBridge.sln
python -m pytest tests/pi -q
```
