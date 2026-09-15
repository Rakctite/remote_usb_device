namespace NetworkHidBridge.Core.Input;

public enum RoutingMode
{
    Default,
    RemoteOnly,
    Shared,
    BlockAll,
}

public readonly record struct RoutingPolicy(bool AllowLocal, bool AllowRemote);

public static class RoutingModeExtensions
{
    public static RoutingPolicy GetPolicy(this RoutingMode mode) => mode switch
    {
        RoutingMode.Default => new(true, false),
        RoutingMode.RemoteOnly => new(false, true),
        RoutingMode.Shared => new(true, true),
        RoutingMode.BlockAll => new(false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
