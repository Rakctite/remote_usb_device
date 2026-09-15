using NetworkHidBridge.Core.Input;

namespace NetworkHidBridge.Core.Tests.Input;

public sealed class CaptureDecisionTests
{
    [Fact]
    public void InjectedKeyboardAndMouseEventsAreNeverSuppressed()
    {
        var blocked = new InputRoutingDecision(true, RemoteInputAction.None);

        Assert.False(CaptureDecision.SuppressKeyboard(injected: true, reserved: false, blocked));
        Assert.False(CaptureDecision.SuppressMouseButton(injected: true, blocked));
        Assert.False(CaptureDecision.SuppressMouseMovement(injected: true, RoutingMode.BlockAll));
    }

    [Fact]
    public void ReservedHotkeyIsAlwaysSuppressedUnlessInjected()
    {
        var allowed = new InputRoutingDecision(false, RemoteInputAction.None);

        Assert.True(CaptureDecision.SuppressKeyboard(injected: false, reserved: true, allowed));
        Assert.False(CaptureDecision.SuppressKeyboard(injected: true, reserved: true, allowed));
    }

    [Theory]
    [InlineData(RoutingMode.Default, false)]
    [InlineData(RoutingMode.RemoteOnly, true)]
    [InlineData(RoutingMode.Shared, false)]
    [InlineData(RoutingMode.BlockAll, true)]
    public void MouseMovementSuppressionUsesLocalRoutingPolicy(RoutingMode mode, bool suppress)
    {
        Assert.Equal(suppress, CaptureDecision.SuppressMouseMovement(false, mode));
    }

    [Fact]
    public void ButtonAndKeyboardSuppressionUsesOwnershipDecision()
    {
        var blocked = new InputRoutingDecision(true, RemoteInputAction.Press);
        var allowed = new InputRoutingDecision(false, RemoteInputAction.None);

        Assert.True(CaptureDecision.SuppressKeyboard(false, false, blocked));
        Assert.False(CaptureDecision.SuppressKeyboard(false, false, allowed));
        Assert.True(CaptureDecision.SuppressMouseButton(false, blocked));
        Assert.False(CaptureDecision.SuppressMouseButton(false, allowed));
    }
}
