namespace NetworkHidBridge.Core.Input;

public enum RemoteInputAction
{
    None,
    Press,
    Release,
}

public readonly record struct InputRoutingDecision(bool SuppressLocal, RemoteInputAction RemoteAction);

public readonly record struct ModeTransitionDecision(bool ReleaseRemoteAll);

public sealed class InputOwnership
{
    private readonly HashSet<int> _physicalDown = [];
    private readonly HashSet<int> _localOwned = [];
    private readonly HashSet<int> _remoteOwned = [];
    private readonly HashSet<int> _localQuarantine = [];
    private readonly HashSet<int> _remoteQuarantine = [];

    public InputRoutingDecision Process(int controlId, bool isDown, RoutingMode mode)
    {
        RoutingPolicy policy = mode.GetPolicy();
        return isDown
            ? ProcessPress(controlId, policy)
            : ProcessRelease(controlId, policy);
    }

    public ModeTransitionDecision ChangeMode(RoutingMode previous, RoutingMode next)
    {
        RoutingPolicy oldPolicy = previous.GetPolicy();
        RoutingPolicy newPolicy = next.GetPolicy();

        if (!oldPolicy.AllowLocal && newPolicy.AllowLocal)
        {
            _localQuarantine.UnionWith(_physicalDown);
        }

        if (!oldPolicy.AllowRemote && newPolicy.AllowRemote)
        {
            _remoteQuarantine.UnionWith(_physicalDown);
        }

        bool releaseRemote = oldPolicy.AllowRemote && !newPolicy.AllowRemote;
        if (releaseRemote)
        {
            _remoteOwned.Clear();
        }

        return new(releaseRemote);
    }

    public void Clear()
    {
        _physicalDown.Clear();
        _localOwned.Clear();
        _remoteOwned.Clear();
        _localQuarantine.Clear();
        _remoteQuarantine.Clear();
    }

    private InputRoutingDecision ProcessPress(int controlId, RoutingPolicy policy)
    {
        bool firstPress = _physicalDown.Add(controlId);

        bool allowLocal = _localOwned.Contains(controlId);
        if (!allowLocal && firstPress && policy.AllowLocal && !_localQuarantine.Contains(controlId))
        {
            _localOwned.Add(controlId);
            allowLocal = true;
        }

        RemoteInputAction remoteAction = RemoteInputAction.None;
        if (firstPress && policy.AllowRemote && !_remoteQuarantine.Contains(controlId))
        {
            _remoteOwned.Add(controlId);
            remoteAction = RemoteInputAction.Press;
        }

        return new(!allowLocal, remoteAction);
    }

    private InputRoutingDecision ProcessRelease(int controlId, RoutingPolicy policy)
    {
        _physicalDown.Remove(controlId);
        _localQuarantine.Remove(controlId);
        _remoteQuarantine.Remove(controlId);

        bool allowLocal = _localOwned.Remove(controlId) || policy.AllowLocal;
        RemoteInputAction remoteAction = _remoteOwned.Remove(controlId)
            ? RemoteInputAction.Release
            : RemoteInputAction.None;

        return new(!allowLocal, remoteAction);
    }
}

public readonly record struct MappedKey(byte Usage, byte ModifierMask);

public sealed class KeyboardReportState
{
    private readonly SortedSet<byte> _pressedUsages = [];
    private byte _modifiers;

    public bool Set(MappedKey key, bool isDown)
    {
        if (key.ModifierMask != 0)
        {
            byte before = _modifiers;
            _modifiers = isDown
                ? (byte)(_modifiers | key.ModifierMask)
                : (byte)(_modifiers & ~key.ModifierMask);
            return before != _modifiers;
        }

        if (key.Usage == 0)
        {
            return false;
        }

        return isDown ? _pressedUsages.Add(key.Usage) : _pressedUsages.Remove(key.Usage);
    }

    public byte[] BuildReport()
    {
        var report = new byte[8];
        report[0] = _modifiers;
        if (_pressedUsages.Count > 6)
        {
            Array.Fill(report, (byte)1, 2, 6);
            return report;
        }

        int index = 2;
        foreach (byte usage in _pressedUsages)
        {
            report[index++] = usage;
        }

        return report;
    }

    public void Clear()
    {
        _modifiers = 0;
        _pressedUsages.Clear();
    }
}
