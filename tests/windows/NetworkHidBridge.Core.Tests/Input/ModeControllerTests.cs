using NetworkHidBridge.Core.Input;

namespace NetworkHidBridge.Core.Tests.Input;

public sealed class ModeControllerTests
{
    [Theory]
    [InlineData(RoutingMode.Default, true, false)]
    [InlineData(RoutingMode.RemoteOnly, false, true)]
    [InlineData(RoutingMode.Shared, true, true)]
    [InlineData(RoutingMode.BlockAll, false, false)]
    public void PoliciesMatchRoutingTruthTable(RoutingMode mode, bool local, bool remote)
    {
        RoutingPolicy policy = mode.GetPolicy();

        Assert.Equal(local, policy.AllowLocal);
        Assert.Equal(remote, policy.AllowRemote);
    }

    [Fact]
    public void StartsInDefaultAndRefusesRemoteModesWithoutAuthentication()
    {
        var controller = new ModeController();

        Assert.Equal(RoutingMode.Default, controller.Mode);
        Assert.False(controller.TrySetMode(RoutingMode.RemoteOnly));
        Assert.False(controller.TrySetMode(RoutingMode.Shared));
        Assert.True(controller.TrySetMode(RoutingMode.BlockAll));
        Assert.Equal(RoutingMode.BlockAll, controller.Mode);
    }

    [Fact]
    public void DisconnectAlwaysRestoresDefault()
    {
        var controller = new ModeController();
        controller.SetAuthenticated(true);
        Assert.True(controller.TrySetMode(RoutingMode.Shared));

        controller.SetAuthenticated(false);

        Assert.Equal(RoutingMode.Default, controller.Mode);
    }

    [Theory]
    [InlineData(RoutingMode.Default, RoutingMode.RemoteOnly)]
    [InlineData(RoutingMode.RemoteOnly, RoutingMode.Default)]
    [InlineData(RoutingMode.Shared, RoutingMode.Default)]
    [InlineData(RoutingMode.BlockAll, RoutingMode.Default)]
    public void NumpadSubtractMultiplyChordTogglesExactlyOnce(
        RoutingMode startingMode,
        RoutingMode expectedMode)
    {
        var controller = new ModeController();
        controller.SetAuthenticated(true);
        Assert.True(controller.TrySetMode(startingMode));

        HotkeyResult first = controller.HandleReservedKey(ModeController.VirtualKeySubtract, true);
        HotkeyResult second = controller.HandleReservedKey(ModeController.VirtualKeyMultiply, true);
        HotkeyResult repeat = controller.HandleReservedKey(ModeController.VirtualKeyMultiply, true);

        Assert.True(first.Consumed);
        Assert.False(first.ModeChanged);
        Assert.True(second.Consumed);
        Assert.True(second.ModeChanged);
        Assert.Equal(expectedMode, second.Mode);
        Assert.True(repeat.Consumed);
        Assert.False(repeat.ModeChanged);
        Assert.Equal(expectedMode, controller.Mode);
    }

    [Fact]
    public void HotkeyWorksInReverseOrderAndRearmsAfterRelease()
    {
        var controller = new ModeController();
        controller.SetAuthenticated(true);

        Assert.True(controller.HandleReservedKey(ModeController.VirtualKeyMultiply, true).Consumed);
        Assert.True(controller.HandleReservedKey(ModeController.VirtualKeySubtract, true).ModeChanged);
        Assert.Equal(RoutingMode.RemoteOnly, controller.Mode);

        Assert.True(controller.HandleReservedKey(ModeController.VirtualKeySubtract, false).Consumed);
        Assert.True(controller.HandleReservedKey(ModeController.VirtualKeySubtract, true).ModeChanged);
        Assert.Equal(RoutingMode.Default, controller.Mode);
    }

    [Fact]
    public void UnrelatedKeyIsNotConsumedByHotkeyController()
    {
        var controller = new ModeController();

        HotkeyResult result = controller.HandleReservedKey(0x41, true);

        Assert.False(result.Consumed);
        Assert.False(result.ModeChanged);
    }
}
