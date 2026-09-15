namespace NetworkHidBridge.Core.Input;

public static class CaptureDecision
{
    public static bool SuppressKeyboard(
        bool injected,
        bool reserved,
        InputRoutingDecision routing) =>
        !injected && (reserved || routing.SuppressLocal);

    public static bool SuppressMouseButton(bool injected, InputRoutingDecision routing) =>
        !injected && routing.SuppressLocal;

    public static bool SuppressMouseMovement(bool injected, RoutingMode mode) =>
        !injected && !mode.GetPolicy().AllowLocal;
}
