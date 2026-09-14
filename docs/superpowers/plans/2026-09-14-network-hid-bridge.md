# Network HID Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows 10/11 tray application that routes physical keyboard and mouse input over an authenticated wired TCP connection to a Raspberry Pi Zero 2 W, which exposes separate keyboard, mouse, and consumer-control USB HID interfaces to a target computer.

**Architecture:** A .NET 9 WPF client captures keyboard input with low-level hooks and mouse deltas with Raw Input, applies explicit local/remote ownership rules, and sends authenticated binary state frames. A Python 3 asyncio server on the Pi validates one client session, maintains watchdog-protected input state, and writes fixed HID reports to a three-function ConfigFS USB Gadget. Shared binary fixtures keep the C# and Python protocol implementations interoperable.

**Tech Stack:** C# 13, .NET 9, WPF, Win32 low-level hooks and Raw Input, xUnit; Python 3.11+, asyncio, pytest; Linux ConfigFS/libcomposite HID Gadget; systemd; TCP with HMAC-SHA256.

---

## Fixed decisions and references

- Source design: `docs/superpowers/specs/2026-09-14-network-hid-bridge-design.md`.
- Default endpoint: `192.168.0.4:45821`; settings may override both values.
- TCP uses `TCP_NODELAY`; protocol confidentiality is outside v1, while authentication and integrity are mandatory.
- Windows is user-mode only. Secure desktop, Ctrl+Alt+Delete, and software protected by incompatible anti-cheat/input policies remain outside scope.
- The reserved hotkey is physical Number Pad Subtract plus Number Pad Multiply. Those keys are always consumed.
- Microsoft references: [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), [Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input), [RegisterRawInputDevices](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices), and [Windows data protection](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection).
- USB reference: [USB HID specifications](https://www.usb.org/hid).

## Repository map

```text
global.json
NetworkHidBridge.sln
README.md
docs/protocol-v1.md
src/windows/NetworkHidBridge.Core/
  NetworkHidBridge.Core.csproj
  Input/{HidUsage.cs,InputOwnership.cs,KeyboardMapper.cs,ModeController.cs,MouseAccumulator.cs,RoutingMode.cs}
  Networking/{BridgeClient.cs,ConnectionState.cs}
  Protocol/{Frame.cs,FrameCodec.cs,MessageType.cs,SessionAuthenticator.cs}
  Settings/{AppSettings.cs,ProtectedSettingsStore.cs,StartupTaskManager.cs}
src/windows/NetworkHidBridge.App/
  NetworkHidBridge.App.csproj
  app.manifest
  App.xaml
  App.xaml.cs
  Capture/{InputCaptureService.cs,KeyboardHook.cs,MouseHook.cs,RawMouseInput.cs}
  MainWindow.xaml
  MainWindow.xaml.cs
  Tray/TrayController.cs
tests/windows/NetworkHidBridge.Core.Tests/
tests/windows/NetworkHidBridge.IntegrationTests/
src/pi/network_hid_bridge/
  __init__.py
  __main__.py
  auth.py
  config.py
  hid.py
  protocol.py
  server.py
  session.py
tests/pi/
tests/fixtures/protocol-v1.json
pyproject.toml
raspi/usb-hid-gadget.sh
raspi/usb-hid-gadget.service
raspi/network-hid-bridge.service
raspi/install.sh
tests/usb-hid-gadget.Tests.ps1
```

## Protocol v1 contract

All integers are big-endian. A frame starts with this 20-byte header:

```text
offset  size  field
0       4     ASCII magic "NHID"
4       1     version = 1
5       1     message type
6       2     flags = 0
8       8     unsigned sequence number
16      4     payload length, maximum 4096
```

Handshake frames have sequence zero and no authentication tag:

- `ClientHello (0x01)`: 32-byte client nonce.
- `ServerHello (0x02)`: 32-byte server nonce followed by `HMAC-SHA256(PSK, "server" || client_nonce || server_nonce)`.
- `ClientAuth (0x03)`: `HMAC-SHA256(PSK, "client" || client_nonce || server_nonce)`.
- Session key: `HMAC-SHA256(PSK, "session" || client_nonce || server_nonce)`.

After authentication, each direction starts at sequence 1 and increments by exactly one. Each frame ends with the first 16 bytes of `HMAC-SHA256(session_key, header || payload)`.

```text
0x10 KeyboardState  payload: 8-byte boot-keyboard report
0x11 MouseState     payload: buttons:u8, dx:i32, dy:i32, wheel:i16, pan:i16
0x12 ConsumerState  payload: usage:u16; zero means released
0x13 FullState      payload: keyboard:8, mouse_buttons:u8, consumer:u16
0x14 ReleaseAll     payload: empty
0x20 Heartbeat      payload: monotonic timestamp:u64
0x21 HeartbeatAck   payload: echoed timestamp:u64
```

Keyboard and consumer changes are immediate. `FullState` is also sent every 500 ms while remote routing is active. Mouse samples are coalesced to at most 125 Hz. The Windows client sends a heartbeat every 250 ms; the Pi releases all inputs and closes the session after 1 second without a valid frame.

## Task 1: Establish toolchains and solution skeleton

**Files:**

- Create: `global.json`
- Create: `NetworkHidBridge.sln`
- Create: `src/windows/NetworkHidBridge.Core/NetworkHidBridge.Core.csproj`
- Create: `src/windows/NetworkHidBridge.App/NetworkHidBridge.App.csproj`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/NetworkHidBridge.Core.Tests.csproj`
- Create: `tests/windows/NetworkHidBridge.IntegrationTests/NetworkHidBridge.IntegrationTests.csproj`
- Create: `pyproject.toml`
- Create: `src/pi/network_hid_bridge/__init__.py`
- Create: `README.md`

- [ ] Verify prerequisites. The current workstation already has .NET SDK 9.0.318 and Python 3.12.

```powershell
dotnet --list-sdks
python --version
```

Expected: SDK `9.0.318` and Python 3.11 or newer. Set `global.json` to SDK `9.0.300` with `rollForward` equal to `latestPatch`, which selects the installed 9.0.318 SDK while staying in the 9.0.3xx feature band.

- [ ] Scaffold the solution and test projects, targeting `net9.0` and `net9.0-windows`. Enable both `UseWPF` and `UseWindowsForms` in the app project so WPF and `NotifyIcon` can coexist.

```powershell
dotnet new sln -n NetworkHidBridge
dotnet new classlib -n NetworkHidBridge.Core -o src/windows/NetworkHidBridge.Core -f net9.0
dotnet new wpf -n NetworkHidBridge.App -o src/windows/NetworkHidBridge.App -f net9.0
dotnet new xunit -n NetworkHidBridge.Core.Tests -o tests/windows/NetworkHidBridge.Core.Tests -f net9.0
dotnet new xunit -n NetworkHidBridge.IntegrationTests -o tests/windows/NetworkHidBridge.IntegrationTests -f net9.0
```

- [ ] Add project references, solution membership, nullable/reference-analysis settings, warnings-as-errors, and xUnit coverage collection. Configure `pyproject.toml` for package discovery under `src/pi`, Python `>=3.11`, and pytest test paths under `tests/pi`.

- [ ] Add a smoke test in each language, then run both suites.

```powershell
dotnet test NetworkHidBridge.sln
python -m pytest tests/pi -q
```

Expected: both suites pass with one smoke test each.

- [ ] Commit.

```powershell
git add global.json NetworkHidBridge.sln README.md pyproject.toml src/windows src/pi tests/windows tests/pi
git commit -m "build: scaffold Windows and Pi applications"
```

## Task 2: Freeze protocol documentation and cross-language fixtures

**Files:**

- Create: `docs/protocol-v1.md`
- Create: `tests/fixtures/protocol-v1.json`
- Create: `tests/pi/test_protocol_fixtures.py`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Protocol/ProtocolFixtureTests.cs`

- [ ] Write failing fixture tests that load the same JSON vectors in Python and C#. Include exact header bytes, all message payload layouts, a fixed PSK/nonces, all three proofs, the session key, a valid authenticated frame, and invalid tag/sequence/oversize cases.

- [ ] Run only the new tests and confirm failure because protocol implementations do not exist.

```powershell
dotnet test tests/windows/NetworkHidBridge.Core.Tests --filter ProtocolFixtureTests
python -m pytest tests/pi/test_protocol_fixtures.py -q
```

Expected: failures identify missing protocol codecs, not malformed fixture JSON.

- [ ] Document the complete contract from the preceding “Protocol v1 contract” section, including disconnect behavior and the 4096-byte payload ceiling. Generate fixture bytes once using an independent one-off calculation and check literal lowercase hex strings into JSON; production code must not generate its own expected values.

- [ ] Commit the red tests and immutable contract.

```powershell
git add docs/protocol-v1.md tests/fixtures tests/pi/test_protocol_fixtures.py tests/windows/NetworkHidBridge.Core.Tests/Protocol
git commit -m "test: define protocol v1 interoperability vectors"
```

## Task 3: Implement protocol and authentication in Python

**Files:**

- Create: `src/pi/network_hid_bridge/protocol.py`
- Create: `src/pi/network_hid_bridge/auth.py`
- Create: `tests/pi/test_protocol.py`
- Create: `tests/pi/test_auth.py`
- Modify: `tests/pi/test_protocol_fixtures.py`

- [ ] Add failing parser tests for fragmented reads, concatenated frames, bad magic/version/flags, unknown type, payloads over 4096 bytes, truncated tags, repeated sequence numbers, and skipped sequence numbers.

- [ ] Add failing authentication tests using deterministic nonces and `hmac.compare_digest`. Verify proof labels are ASCII bytes and nonce order is always client then server.

- [ ] Implement immutable `Frame`, `MessageType`, `ProtocolError`, exact-length async reads, frame serialization/parsing, and a sequence tracker. Never allocate a payload until its declared length has passed the maximum-size check.

- [ ] Implement server-side handshake and tag verification with `hashlib.sha256`, `hmac.new`, and `secrets.token_bytes(32)`.

- [ ] Run protocol tests.

```powershell
python -m pytest tests/pi/test_protocol.py tests/pi/test_auth.py tests/pi/test_protocol_fixtures.py -q
```

Expected: all Python protocol and fixture tests pass.

- [ ] Commit.

```powershell
git add src/pi/network_hid_bridge/protocol.py src/pi/network_hid_bridge/auth.py tests/pi
git commit -m "feat(pi): implement authenticated protocol codec"
```

## Task 4: Implement the same protocol in C#

**Files:**

- Create: `src/windows/NetworkHidBridge.Core/Protocol/MessageType.cs`
- Create: `src/windows/NetworkHidBridge.Core/Protocol/Frame.cs`
- Create: `src/windows/NetworkHidBridge.Core/Protocol/FrameCodec.cs`
- Create: `src/windows/NetworkHidBridge.Core/Protocol/SessionAuthenticator.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Protocol/FrameCodecTests.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Protocol/SessionAuthenticatorTests.cs`
- Modify: `tests/windows/NetworkHidBridge.Core.Tests/Protocol/ProtocolFixtureTests.cs`

- [ ] Add failing unit tests matching every Python parser/authentication boundary, including segmented `Stream` reads and cancellation.

- [ ] Implement encoding with `BinaryPrimitives`, constant-time proof/tag comparison with `CryptographicOperations.FixedTimeEquals`, bounded payload allocation, and exact independent send/receive sequences.

- [ ] Run C# protocol tests and the shared fixtures.

```powershell
dotnet test tests/windows/NetworkHidBridge.Core.Tests --filter "FullyQualifiedName~Protocol"
python -m pytest tests/pi/test_protocol_fixtures.py -q
```

Expected: both implementations accept the same valid vectors and reject the same invalid vectors.

- [ ] Commit.

```powershell
git add src/windows/NetworkHidBridge.Core/Protocol tests/windows/NetworkHidBridge.Core.Tests/Protocol
git commit -m "feat(windows): implement authenticated protocol codec"
```

## Task 5: Extend the USB Gadget to three stable HID interfaces

**Files:**

- Modify: `raspi/usb-hid-gadget.sh`
- Modify: `raspi/usb-hid-gadget.service`
- Modify: `tests/usb-hid-gadget.Tests.ps1`

- [ ] Extend the PowerShell structure test first. Require `01-keyboard`, `02-mouse`, `03-consumer`, report lengths `8`, `5`, and `2`, a node-count assertion for `/dev/hidg0` through `/dev/hidg2`, cleanup of all three functions, and service ordering needed by the receiver.

- [ ] Run the structure test and observe failure on the missing consumer interface and four-byte mouse descriptor.

```powershell
pwsh -File tests/usb-hid-gadget.Tests.ps1
```

- [ ] Update keyboard descriptor to retain boot protocol and admit usages through `0xA4` for F13-F24. Its input report remains modifier byte, reserved byte, and six usages.

- [ ] Update mouse descriptor to five buttons plus signed 8-bit X, Y, vertical wheel, and AC Pan. Set `report_length` to `5`.

- [ ] Add a Consumer Control function with protocol/subclass zero, a two-byte unsigned usage report, usage range `0x0000..0x03FF`, and stable link name `03-consumer`.

- [ ] After binding the UDC, poll for exactly `/dev/hidg0`, `/dev/hidg1`, and `/dev/hidg2`; fail the oneshot service if any expected node is absent. Set group `hidbridge` and mode `0660` on each node without weakening other device permissions.

- [ ] Run the structure test, then deploy to the Pi and verify descriptors and enumeration.

```powershell
pwsh -File tests/usb-hid-gadget.Tests.ps1
scp raspi/usb-hid-gadget.sh iotsys@192.168.0.4:/tmp/usb-hid-gadget.sh
ssh iotsys@192.168.0.4 "sudo install -m 0755 /tmp/usb-hid-gadget.sh /usr/local/sbin/usb-hid-gadget && sudo systemctl restart usb-hid-gadget && ls -l /dev/hidg0 /dev/hidg1 /dev/hidg2"
```

Expected: service is active; all three nodes exist with group `hidbridge`; Windows Device Manager shows one composite device and three HID interfaces. A target re-enumeration is expected because descriptors changed.

- [ ] Commit.

```powershell
git add raspi/usb-hid-gadget.sh raspi/usb-hid-gadget.service tests/usb-hid-gadget.Tests.ps1
git commit -m "feat(pi): expose keyboard mouse and consumer HID interfaces"
```

## Task 6: Implement safe Pi HID output

**Files:**

- Create: `src/pi/network_hid_bridge/hid.py`
- Create: `tests/pi/test_hid.py`

- [ ] Write failing tests against temporary files for keyboard reports, five-button mouse reports, consumer reports, release-all, and shutdown release. Add parameterized large deltas such as `300`, `-300`, `32767`, and `-32768`; verify each emitted axis/wheel/pan byte is within `-127..127` and the sum is preserved.

- [ ] Implement a `HidOutput` context manager owning three unbuffered binary handles. Use a lock around report groups, clamp and split accumulated relative motion, and write empty reports as keyboard `00 00 00 00 00 00 00 00`, mouse `00 00 00 00 00`, consumer `00 00`.

- [ ] Treat short writes, missing nodes, and `OSError` as fatal output failures so the server can terminate the session and attempt release-all.

- [ ] Run tests.

```powershell
python -m pytest tests/pi/test_hid.py -q
```

Expected: all report-size, splitting, and release tests pass.

- [ ] Commit.

```powershell
git add src/pi/network_hid_bridge/hid.py tests/pi/test_hid.py
git commit -m "feat(pi): write bounded HID reports safely"
```

## Task 7: Build the watchdog-protected Pi session and server

**Files:**

- Create: `src/pi/network_hid_bridge/config.py`
- Create: `src/pi/network_hid_bridge/session.py`
- Create: `src/pi/network_hid_bridge/server.py`
- Create: `src/pi/network_hid_bridge/__main__.py`
- Create: `tests/pi/test_config.py`
- Create: `tests/pi/test_session.py`
- Create: `tests/pi/test_server.py`

- [ ] Write failing tests for valid message dispatch, state synchronization, heartbeat acknowledgement, one-second timeout, disconnect, invalid HMAC after active input, malformed state length, and cancellation. Every termination path must assert three empty HID reports.

- [ ] Add a two-client test: while an authenticated session owns output, the second connection receives no handshake acceptance and is closed; after release, the next client can authenticate.

- [ ] Implement strict JSON configuration with `listen_address`, `port`, `psk`, and three HID paths. Reject missing/empty PSKs, wildcard bind addresses, invalid ports, and unknown properties. Defaults are explicit in the installer-generated file, not silently supplied for security-sensitive fields.

- [ ] Implement a single-owner asyncio server. Authenticate before opening control flow, dispatch exact payload lengths, acknowledge heartbeats, update last-valid-frame time, and use a watchdog task. Log lifecycle and error categories only; never log payloads, PSK material, keys, buttons, movement, or HID reports.

- [ ] Run the full Python suite.

```powershell
python -m pytest tests/pi -q
```

Expected: all Pi tests pass, including release-all on every tested exit path.

- [ ] Commit.

```powershell
git add src/pi/network_hid_bridge tests/pi
git commit -m "feat(pi): add authenticated HID bridge server"
```

## Task 8: Implement Windows routing state and HID mappings

**Files:**

- Create: `src/windows/NetworkHidBridge.Core/Input/RoutingMode.cs`
- Create: `src/windows/NetworkHidBridge.Core/Input/InputOwnership.cs`
- Create: `src/windows/NetworkHidBridge.Core/Input/HidUsage.cs`
- Create: `src/windows/NetworkHidBridge.Core/Input/KeyboardMapper.cs`
- Create: `src/windows/NetworkHidBridge.Core/Input/ModeController.cs`
- Create: `src/windows/NetworkHidBridge.Core/Input/MouseAccumulator.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Input/ModeControllerTests.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Input/InputOwnershipTests.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Input/KeyboardMapperTests.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Input/MouseAccumulatorTests.cs`

- [ ] Encode the four-mode truth table as failing tests. Verify startup Default, disconnected fallback Default, refusal of Remote only/Shared while unauthenticated, and direct UI selection of Shared/Block all.

- [ ] Test the reserved hotkey as a chord independent of press order and key-repeat. Default toggles to Remote only; every other mode toggles to Default. Both reserved keys remain suppressed on keydown and keyup and never enter local or remote reports.

- [ ] Test ownership across transitions: locally delivered downs receive local ups after local is disabled; remotely delivered state receives release-all when remote is disabled; controls physically held when remote becomes enabled are quarantined until released and pressed again.

- [ ] Add table-driven Windows scan-code plus extended-bit mappings for modifiers, letters, digits, punctuation, navigation, number pad, Print Screen, Pause, and F1-F24. Explicitly reject unmapped keys rather than forwarding an invalid usage.

- [ ] Implement an allocation-free mouse accumulator using atomic exchange for X/Y/wheel/pan and a separately synchronized five-button snapshot.

- [ ] Run input tests.

```powershell
dotnet test tests/windows/NetworkHidBridge.Core.Tests --filter "FullyQualifiedName~Input"
```

Expected: the routing truth table, hotkey, ownership, mapping, and accumulation suites pass.

- [ ] Commit.

```powershell
git add src/windows/NetworkHidBridge.Core/Input tests/windows/NetworkHidBridge.Core.Tests/Input
git commit -m "feat(windows): implement input routing and HID mapping"
```

## Task 9: Implement Windows connection lifecycle

**Files:**

- Create: `src/windows/NetworkHidBridge.Core/Networking/ConnectionState.cs`
- Create: `src/windows/NetworkHidBridge.Core/Networking/BridgeClient.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Networking/BridgeClientTests.cs`
- Create: `tests/windows/NetworkHidBridge.IntegrationTests/LoopbackBridgeTests.cs`

- [ ] Write tests around a loopback fake server for authentication success/failure, `TCP_NODELAY`, single serialized writer, 250 ms heartbeat, acknowledged round-trip time, reconnect backoff, cancellation, and exact disconnect notification once.

- [ ] Verify input scheduling: keyboard/consumer changes send immediately, mouse sends no faster than every 8 ms, FullState sends every 500 ms only during remote routing, and remote disable queues ReleaseAll ahead of discarded state.

- [ ] Implement one bounded outbound channel and one writer task so frame ordering and send sequence cannot race. Use reconnect delays `250 ms, 500 ms, 1 s, 2 s, 5 s`, capped at 5 seconds and reset after authentication.

- [ ] Surface immutable connection snapshots containing state, endpoint, last error category, and optional RTT. Ensure socket failure calls the mode controller synchronously enough to restore Default before a later input callback can suppress local input.

- [ ] Run tests.

```powershell
dotnet test tests/windows/NetworkHidBridge.Core.Tests --filter "FullyQualifiedName~Networking"
dotnet test tests/windows/NetworkHidBridge.IntegrationTests
```

Expected: all lifecycle and loopback tests pass without timing flakes across five consecutive runs.

- [ ] Commit.

```powershell
git add src/windows/NetworkHidBridge.Core/Networking tests/windows
git commit -m "feat(windows): add reconnecting bridge client"
```

## Task 10: Add Windows physical-input capture and suppression

**Files:**

- Create: `src/windows/NetworkHidBridge.App/Capture/KeyboardHook.cs`
- Create: `src/windows/NetworkHidBridge.App/Capture/MouseHook.cs`
- Create: `src/windows/NetworkHidBridge.App/Capture/RawMouseInput.cs`
- Create: `src/windows/NetworkHidBridge.App/Capture/InputCaptureService.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Input/CaptureDecisionTests.cs`

- [ ] Extract and test a pure capture-decision function for keyboard suppression, mouse suppression, injected-event rejection, ownership overrides, hotkey consumption, and disconnected fallback.

- [ ] Implement `WH_KEYBOARD_LL` and `WH_MOUSE_LL` hooks on a dedicated message-loop thread. Keep delegates rooted for hook lifetime, ignore `LLKHF_INJECTED`/`LLMHF_INJECTED`, perform only state update/enqueue/suppression decisions in callbacks, and always unhook during orderly disposal.

- [ ] Register one hidden message-only window for Raw Input mouse with `RIDEV_INPUTSINK`. Raw Input is authoritative for remote movement, buttons, vertical wheel, and horizontal wheel; the low-level mouse hook is authoritative only for local suppression and ownership releases. Deduplicate button delivery accordingly.

- [ ] Treat hook-registration or Raw Input registration failure as fatal application startup failure with a clear UI message. Never enter a mode that blocks local input unless all required capture components and the authenticated remote connection are healthy.

- [ ] Run unit tests, build x64 Release, and perform a manual capture test in Notepad before connecting the target: Default types locally; Block all suppresses new input; existing held keys/buttons release locally; the hotkey always restores Default.

```powershell
dotnet test tests/windows/NetworkHidBridge.Core.Tests --filter CaptureDecisionTests
dotnet build src/windows/NetworkHidBridge.App -c Release -p:Platform=x64
```

Expected: tests and build pass; the manual escape-path check succeeds.

- [ ] Commit.

```powershell
git add src/windows/NetworkHidBridge.App/Capture tests/windows/NetworkHidBridge.Core.Tests/Input
git commit -m "feat(windows): capture and suppress physical input"
```

## Task 11: Add protected settings, elevated startup, tray, and WPF UI

**Files:**

- Create: `src/windows/NetworkHidBridge.Core/Settings/AppSettings.cs`
- Create: `src/windows/NetworkHidBridge.Core/Settings/ProtectedSettingsStore.cs`
- Create: `src/windows/NetworkHidBridge.Core/Settings/StartupTaskManager.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Settings/ProtectedSettingsStoreTests.cs`
- Create: `tests/windows/NetworkHidBridge.Core.Tests/Settings/StartupTaskManagerTests.cs`
- Create: `src/windows/NetworkHidBridge.App/app.manifest`
- Modify: `src/windows/NetworkHidBridge.App/NetworkHidBridge.App.csproj`
- Modify: `src/windows/NetworkHidBridge.App/App.xaml`
- Modify: `src/windows/NetworkHidBridge.App/App.xaml.cs`
- Create: `src/windows/NetworkHidBridge.App/MainWindow.xaml`
- Create: `src/windows/NetworkHidBridge.App/MainWindow.xaml.cs`
- Create: `src/windows/NetworkHidBridge.App/Tray/TrayController.cs`

- [ ] Write settings tests for default endpoint, invalid IP/port, atomic replacement, corrupt-file recovery, and PSK encryption/decryption scoped to `DataProtectionScope.CurrentUser`. Store at `%LocalAppData%\IoTSys\NetworkHidBridge\settings.json`; the JSON must never contain the plaintext PSK.

- [ ] Add the `System.Security.Cryptography.ProtectedData` 9.x package. Implement temp-file plus atomic replace persistence and redact secrets from exceptions and UI logs.

- [ ] Write tests for deterministic Task Scheduler command construction. Use a stable task name `IoTSys Network HID Bridge`, `/SC ONLOGON`, `/RL HIGHEST`, current user, and the quoted published executable path. Query the task after create/delete and report actionable errors.

- [ ] Add an `asInvoker`-independent admin manifest using `requireAdministrator`; the startup task supplies elevation at logon. Ensure only one process instance owns the global hooks by a named mutex scoped to the interactive user session.

- [ ] Implement the settings window and tray menu. Show connection state, current mode, endpoint, RTT, reconnect, startup toggle, and diagnostics. Tray mode entries must call the same `ModeController` as the hotkey. Closing the window hides it; only tray Exit cancels services, sends ReleaseAll when possible, disposes hooks, and terminates.

- [ ] Add a bounded in-memory diagnostic log containing timestamps and lifecycle/error categories only. Verify no key, mouse, report, nonce, tag, or PSK bytes reach it.

- [ ] Run tests and build.

```powershell
dotnet test NetworkHidBridge.sln
dotnet build src/windows/NetworkHidBridge.App -c Release -p:Platform=x64
```

Expected: all tests pass and the executable requests elevation, minimizes to tray, and exits only through the explicit command.

- [ ] Commit.

```powershell
git add src/windows tests/windows
git commit -m "feat(windows): add secure settings and tray application"
```

## Task 12: Package and install the Pi services

**Files:**

- Create: `raspi/network-hid-bridge.service`
- Create: `raspi/install.sh`
- Create: `tests/pi/test_install_assets.py`
- Modify: `README.md`

- [ ] Add static asset tests requiring a locked-down `hidbridge` system account/group, `/opt/network-hid-bridge`, config mode `0640` owned `root:hidbridge`, service `User=hidbridge`, `Group=hidbridge`, `Requires=usb-hid-gadget.service`, `After=usb-hid-gadget.service network-online.target`, restart policy, and preflight checks for all three HID nodes.

- [ ] Implement an idempotent installer that accepts endpoint/PSK interactively or via explicit flags, refuses an empty PSK, installs Python source without network-time dependencies, writes the config atomically, installs both unit files/scripts, reloads systemd, and enables both services. It must not alter Wi-Fi, Bluetooth, boot overlays, or Ethernet configuration.

- [ ] Add systemd hardening compatible with HID writes and network listening: `NoNewPrivileges=true`, `PrivateTmp=true`, `ProtectSystem=strict`, `ProtectHome=true`, `ReadWritePaths=/dev/hidg0 /dev/hidg1 /dev/hidg2`, and a restrictive `UMask=0077`. Validate these directives on the deployed Pi with `systemd-analyze verify`.

- [ ] Document initial manual-IP configuration for the eventual SPI Ethernet interface, making clear that interface naming and NetworkManager/systemd-networkd ownership must first be inspected on the deployed Raspberry Pi OS image. Keep application bind address at `192.168.0.4` only after the interface actually owns that address.

- [ ] Run static tests, deploy, and inspect service health.

```powershell
python -m pytest tests/pi/test_install_assets.py -q
scp -r src/pi raspi iotsys@192.168.0.4:/tmp/network-hid-bridge-deploy
ssh iotsys@192.168.0.4 "cd /tmp/network-hid-bridge-deploy && sudo bash raspi/install.sh --listen-address 192.168.0.4 --port 45821"
ssh iotsys@192.168.0.4 "sudo systemd-analyze verify /etc/systemd/system/usb-hid-gadget.service /etc/systemd/system/network-hid-bridge.service && systemctl --no-pager --full status usb-hid-gadget network-hid-bridge"
```

Expected: unit verification succeeds and both services are active after reboot. Enter the PSK interactively so it does not appear in shell history.

- [ ] Commit.

```powershell
git add raspi src/pi tests/pi README.md
git commit -m "build(pi): add hardened systemd installation"
```

## Task 13: Prove C# to Python interoperability without USB hardware

**Files:**

- Create: `tests/pi/fake_hid_server.py`
- Modify: `tests/windows/NetworkHidBridge.IntegrationTests/LoopbackBridgeTests.cs`
- Create: `tests/windows/NetworkHidBridge.IntegrationTests/PythonInteropTests.cs`
- Modify: `README.md`

- [ ] Write an integration test that launches `python -m network_hid_bridge` with loopback configuration and temporary fake HID files, authenticates through the production C# client, sends keyboard/mouse/consumer state, and checks exact file report bytes.

- [ ] Add failure cases for wrong PSK, replayed authenticated frame, client process termination, heartbeat timeout, and reconnect with a clean state. Each case must end with empty keyboard, mouse, and consumer reports.

- [ ] Run the interop test repeatedly to expose lifecycle races.

```powershell
1..5 | ForEach-Object { dotnet test tests/windows/NetworkHidBridge.IntegrationTests --filter PythonInteropTests }
```

Expected: five clean passes with no orphan Python process and empty fake HID reports after each test.

- [ ] Commit.

```powershell
git add tests/pi/fake_hid_server.py tests/windows/NetworkHidBridge.IntegrationTests README.md
git commit -m "test: verify Windows and Pi interoperability"
```

## Task 14: Hardware acceptance and release build

**Files:**

- Create: `docs/hardware-acceptance.md`
- Modify: `README.md`

- [ ] Run all automated verification from a clean build.

```powershell
dotnet clean NetworkHidBridge.sln
dotnet test NetworkHidBridge.sln -c Release
python -m pytest tests/pi -q
pwsh -File tests/usb-hid-gadget.Tests.ps1
git diff --check
```

Expected: zero failures and no whitespace errors.

- [ ] Publish a self-contained x64 Windows build and record its SHA-256 checksum.

```powershell
dotnet publish src/windows/NetworkHidBridge.App -c Release -r win-x64 --self-contained true -o artifacts/windows-x64
Get-FileHash artifacts/windows-x64/NetworkHidBridge.App.exe -Algorithm SHA256
```

- [ ] On the target PC, verify Device Manager shows one composite device with three HID functions. Exercise normal keys, all modifiers, navigation, number pad, F1-F24, five mouse buttons, X/Y, vertical/horizontal wheel, and the defined consumer controls.

- [ ] Verify every mode and transition, including local ownership releases, remote quarantine of already-held controls, Default-to-Remote-only hotkey, every-non-default-to-Default hotkey, and tray mode selection. Confirm the reserved keys never reach either PC.

- [ ] Verify failures: unplug Ethernet, unplug target USB, kill the Windows client, kill the Pi daemon, use a wrong PSK, and reboot the Pi. Confirm local routing returns to Default and target HID state releases within one second wherever the physical USB link remains present.

- [ ] Manually verify basic keyboard and pointer behavior in BIOS/UEFI. Record target motherboard/firmware and any limitations; extended controls are not acceptance requirements there.

- [ ] Inspect logs on both systems and confirm they contain lifecycle/error data but no input content or secret material.

- [ ] Complete `docs/hardware-acceptance.md` with date, hardware versions, OS versions, pass/fail for each check, and any documented limitation. Update README with install, configure, operate, recover, and uninstall instructions.

- [ ] Commit release evidence.

```powershell
git add README.md docs/hardware-acceptance.md
git commit -m "docs: record hardware acceptance and operation"
```

## Final review gate

- [ ] Compare every section of the design spec against this implementation and the acceptance record: hardware, three HID interfaces, four routing modes, hotkey semantics, protocol/authentication, failure handling, tray UX, Pi operations, and exclusions.

- [ ] Scan tracked source and documentation for unresolved placeholder language without making the scan pattern match this plan line itself.

```powershell
rg -n "T[B]D|T[O]DO|F[I]XME|PLACEHOLD[E]R|similar t[o]|appropriate erro[r]" .
```

Expected: no matches in tracked project files.

- [ ] Verify clean repository state and inspect the final history.

```powershell
git diff --check
git status --short
git log --oneline --decorate -15
```

Expected: no unintended changes; all task commits are present and ordered.
