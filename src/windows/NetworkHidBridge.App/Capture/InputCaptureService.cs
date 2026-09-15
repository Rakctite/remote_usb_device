using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;

namespace NetworkHidBridge.App.Capture;

internal sealed class InputCaptureService : IDisposable
{
    private const int MouseControlBase = 0x01000000;
    private readonly ModeController _modeController;
    private readonly BridgeClient _bridgeClient;
    private readonly InputOwnership _keyboardOwnership = new();
    private readonly InputOwnership _localMouseOwnership = new();
    private readonly InputOwnership _remoteMouseOwnership = new();
    private readonly KeyboardReportState _keyboardState = new();
    private readonly KeyboardHook _keyboardHook;
    private readonly MouseHook _mouseHook;
    private readonly RawMouseInput _rawMouse;
    private RoutingMode _lastMode;
    private bool _started;

    public InputCaptureService(ModeController modeController, BridgeClient bridgeClient)
    {
        _modeController = modeController ?? throw new ArgumentNullException(nameof(modeController));
        _bridgeClient = bridgeClient ?? throw new ArgumentNullException(nameof(bridgeClient));
        _lastMode = modeController.Mode;
        _keyboardHook = new(HandleKeyboard);
        _mouseHook = new(HandleMouseHook);
        _rawMouse = new(HandleRawMouse);
    }

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("Input capture is already started.");
        }
        try
        {
            _rawMouse.Start();
            _keyboardHook.Start();
            _mouseHook.Start();
            _modeController.ModeChanged += HandleModeChanged;
            _started = true;
        }
        catch
        {
            _mouseHook.Dispose();
            _keyboardHook.Dispose();
            _rawMouse.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _modeController.TrySetMode(RoutingMode.Default);
            _modeController.ModeChanged -= HandleModeChanged;
        }
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        _rawMouse.Dispose();
        _started = false;
    }

    private bool HandleKeyboard(KeyboardHookEvent value)
    {
        if (value.Injected)
        {
            return false;
        }

        HotkeyResult hotkey = _modeController.HandleReservedKey(value.VirtualKey, value.IsDown);
        if (hotkey.Consumed)
        {
            return CaptureDecision.SuppressKeyboard(false, reserved: true, default);
        }

        int controlId = CreateKeyboardControlId(value);
        InputRoutingDecision routing = _keyboardOwnership.Process(controlId, value.IsDown, _modeController.Mode);
        if (routing.RemoteAction != RemoteInputAction.None)
        {
            bool remoteDown = routing.RemoteAction == RemoteInputAction.Press;
            if (KeyboardMapper.TryMapConsumer(value.VirtualKey, out ushort consumerUsage))
            {
                _bridgeClient.QueueConsumer(remoteDown ? consumerUsage : (ushort)0);
            }
            else if (KeyboardMapper.TryMap(value.VirtualKey, value.ScanCode, value.Extended, out MappedKey mapped)
                && _keyboardState.Set(mapped, remoteDown))
            {
                _bridgeClient.QueueKeyboard(_keyboardState.BuildReport());
            }
        }
        return CaptureDecision.SuppressKeyboard(false, reserved: false, routing);
    }

    private bool HandleMouseHook(MouseHookEvent value)
    {
        if (value.Kind != MouseHookKind.Button)
        {
            return CaptureDecision.SuppressMouseMovement(value.Injected, _modeController.Mode);
        }
        if (value.Injected)
        {
            return false;
        }
        InputRoutingDecision routing = _localMouseOwnership.Process(
            MouseControlBase + value.Button,
            value.IsDown,
            _modeController.Mode);
        return CaptureDecision.SuppressMouseButton(false, routing);
    }

    private void HandleRawMouse(RawMousePacket packet)
    {
        _bridgeClient.AddMouseMovement(packet.DeltaX, packet.DeltaY);
        if ((packet.ButtonFlags & 0x0400) != 0)
        {
            _bridgeClient.AddMouseWheel(packet.ButtonData, 0);
        }
        if ((packet.ButtonFlags & 0x0800) != 0)
        {
            _bridgeClient.AddMouseWheel(0, packet.ButtonData);
        }

        for (int button = 1; button <= 5; button++)
        {
            ushort downFlag = (ushort)(1 << ((button - 1) * 2));
            ushort upFlag = (ushort)(downFlag << 1);
            if ((packet.ButtonFlags & downFlag) != 0)
            {
                RouteRawButton(button, true);
            }
            if ((packet.ButtonFlags & upFlag) != 0)
            {
                RouteRawButton(button, false);
            }
        }
    }

    private void RouteRawButton(int button, bool isDown)
    {
        InputRoutingDecision routing = _remoteMouseOwnership.Process(
            MouseControlBase + button,
            isDown,
            _modeController.Mode);
        if (routing.RemoteAction == RemoteInputAction.Press)
        {
            _bridgeClient.SetMouseButton(button, true);
        }
        else if (routing.RemoteAction == RemoteInputAction.Release)
        {
            _bridgeClient.SetMouseButton(button, false);
        }
    }

    private void HandleModeChanged(object? sender, RoutingMode mode)
    {
        ModeTransitionDecision keyboard = _keyboardOwnership.ChangeMode(_lastMode, mode);
        ModeTransitionDecision rawMouse = _remoteMouseOwnership.ChangeMode(_lastMode, mode);
        _localMouseOwnership.ChangeMode(_lastMode, mode);
        bool remote = mode.GetPolicy().AllowRemote;
        if (!remote && (keyboard.ReleaseRemoteAll || rawMouse.ReleaseRemoteAll))
        {
            _keyboardState.Clear();
        }
        _bridgeClient.SetRemoteRouting(remote);
        _lastMode = mode;
    }

    private static int CreateKeyboardControlId(KeyboardHookEvent value) =>
        unchecked((int)((value.VirtualKey << 16) | (value.ScanCode & 0x7FFF) | (value.Extended ? 0x8000u : 0)));
}
