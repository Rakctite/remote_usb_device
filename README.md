# Network HID Bridge

This repository contains the early skeleton for two cooperating components:

- a .NET 9 Windows desktop application under `src/windows`; and
- a Python 3.11+ Raspberry Pi receiver package under `src/pi`.

The transport, input-routing, authentication, and USB HID behavior described in the design documents are not implemented yet.

Install the Python test tooling, then build and test the complete scaffold from the repository root:

```powershell
python -m pip install -e ".[test]"
dotnet build NetworkHidBridge.sln
dotnet test NetworkHidBridge.sln
python -m pytest tests/pi -q
```
