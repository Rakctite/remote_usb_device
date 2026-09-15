namespace NetworkHidBridge.Core.Input;

public readonly record struct HotkeyResult(bool Consumed, bool ModeChanged, RoutingMode Mode);

public sealed class ModeController
{
    public const uint VirtualKeyMultiply = 0x6A;
    public const uint VirtualKeySubtract = 0x6D;

    private bool _multiplyDown;
    private bool _subtractDown;
    private bool _hotkeyLatched;

    public RoutingMode Mode { get; private set; } = RoutingMode.Default;

    public bool IsAuthenticated { get; private set; }

    public event EventHandler<RoutingMode>? ModeChanged;

    public void SetAuthenticated(bool authenticated)
    {
        IsAuthenticated = authenticated;
        if (!authenticated)
        {
            SetModeCore(RoutingMode.Default);
        }
    }

    public bool TrySetMode(RoutingMode mode)
    {
        _ = mode.GetPolicy();
        if (mode.GetPolicy().AllowRemote && !IsAuthenticated)
        {
            return false;
        }

        SetModeCore(mode);
        return true;
    }

    public HotkeyResult HandleReservedKey(uint virtualKey, bool isDown)
    {
        if (virtualKey is not (VirtualKeyMultiply or VirtualKeySubtract))
        {
            return new(false, false, Mode);
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
            RoutingMode requested = Mode == RoutingMode.Default
                ? RoutingMode.RemoteOnly
                : RoutingMode.Default;
            changed = TrySetMode(requested);
            _hotkeyLatched = true;
        }
        else if (!chordDown)
        {
            _hotkeyLatched = false;
        }

        return new(true, changed, Mode);
    }

    private void SetModeCore(RoutingMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        ModeChanged?.Invoke(this, mode);
    }
}
