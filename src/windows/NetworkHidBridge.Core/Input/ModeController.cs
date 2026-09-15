namespace NetworkHidBridge.Core.Input;

public readonly record struct HotkeyResult(bool Consumed, bool ModeChanged, RoutingMode Mode);

public sealed class ModeController
{
    public const uint VirtualKeyMultiply = 0x6A;
    public const uint VirtualKeySubtract = 0x6D;

    private readonly object _sync = new();
    private bool _multiplyDown;
    private bool _subtractDown;
    private bool _hotkeyLatched;
    private RoutingMode _mode = RoutingMode.Default;
    private bool _isAuthenticated;

    public RoutingMode Mode
    {
        get
        {
            lock (_sync)
            {
                return _mode;
            }
        }
    }

    public bool IsAuthenticated
    {
        get
        {
            lock (_sync)
            {
                return _isAuthenticated;
            }
        }
    }

    public event EventHandler<RoutingMode>? ModeChanged;

    public void SetAuthenticated(bool authenticated)
    {
        lock (_sync)
        {
            _isAuthenticated = authenticated;
            if (!authenticated)
            {
                SetModeCore(RoutingMode.Default);
            }
        }
    }

    public bool TrySetMode(RoutingMode mode)
    {
        _ = mode.GetPolicy();
        lock (_sync)
        {
            if (mode.GetPolicy().AllowRemote && !_isAuthenticated)
            {
                return false;
            }

            SetModeCore(mode);
            return true;
        }
    }

    public HotkeyResult HandleReservedKey(uint virtualKey, bool isDown)
    {
        lock (_sync)
        {
            if (virtualKey is not (VirtualKeyMultiply or VirtualKeySubtract))
            {
                return new(false, false, _mode);
            }

            if (virtualKey == VirtualKeyMultiply)
            {
                _multiplyDown = isDown;
            }
            else
            {
                _subtractDown = isDown;
            }

            bool chordDown = _multiplyDown && _subtractDown;
            bool changed = false;
            if (chordDown && !_hotkeyLatched)
            {
                RoutingMode requested = _mode == RoutingMode.Default
                    ? RoutingMode.RemoteOnly
                    : RoutingMode.Default;
                changed = TrySetMode(requested);
                _hotkeyLatched = true;
            }
            else if (!chordDown)
            {
                _hotkeyLatched = false;
            }

            return new(true, changed, _mode);
        }
    }

    private void SetModeCore(RoutingMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        ModeChanged?.Invoke(this, mode);
    }
}
