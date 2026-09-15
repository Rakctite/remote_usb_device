using NetworkHidBridge.Core.Input;

namespace NetworkHidBridge.Core.Tests.Input;

public sealed class InputOwnershipTests
{
    [Fact]
    public void DefaultRoutesOnlyToLocalComputer()
    {
        var ownership = new InputOwnership();

        InputRoutingDecision down = ownership.Process(30, true, RoutingMode.Default);
        InputRoutingDecision up = ownership.Process(30, false, RoutingMode.Default);

        Assert.False(down.SuppressLocal);
        Assert.Equal(RemoteInputAction.None, down.RemoteAction);
        Assert.False(up.SuppressLocal);
        Assert.Equal(RemoteInputAction.None, up.RemoteAction);
    }

    [Fact]
    public void RemoteOnlySuppressesLocalAndRoutesRemote()
    {
        var ownership = new InputOwnership();

        InputRoutingDecision down = ownership.Process(30, true, RoutingMode.RemoteOnly);
        InputRoutingDecision up = ownership.Process(30, false, RoutingMode.RemoteOnly);

        Assert.True(down.SuppressLocal);
        Assert.Equal(RemoteInputAction.Press, down.RemoteAction);
        Assert.True(up.SuppressLocal);
        Assert.Equal(RemoteInputAction.Release, up.RemoteAction);
    }

    [Fact]
    public void LocalPressKeepsOwnershipUntilReleaseAfterModeChange()
    {
        var ownership = new InputOwnership();
        ownership.Process(30, true, RoutingMode.Default);

        ModeTransitionDecision transition = ownership.ChangeMode(RoutingMode.Default, RoutingMode.RemoteOnly);
        InputRoutingDecision release = ownership.Process(30, false, RoutingMode.RemoteOnly);

        Assert.False(transition.ReleaseRemoteAll);
        Assert.False(release.SuppressLocal);
        Assert.Equal(RemoteInputAction.None, release.RemoteAction);
    }

    [Fact]
    public void HeldControlIsQuarantinedWhenRemoteBecomesEnabled()
    {
        var ownership = new InputOwnership();
        ownership.Process(30, true, RoutingMode.Default);

        ownership.ChangeMode(RoutingMode.Default, RoutingMode.RemoteOnly);
        InputRoutingDecision repeat = ownership.Process(30, true, RoutingMode.RemoteOnly);
        InputRoutingDecision release = ownership.Process(30, false, RoutingMode.RemoteOnly);
        InputRoutingDecision freshPress = ownership.Process(30, true, RoutingMode.RemoteOnly);

        Assert.Equal(RemoteInputAction.None, repeat.RemoteAction);
        Assert.Equal(RemoteInputAction.None, release.RemoteAction);
        Assert.Equal(RemoteInputAction.Press, freshPress.RemoteAction);
    }

    [Fact]
    public void DisablingRemoteRequestsReleaseAllAndClearsRemoteOwnership()
    {
        var ownership = new InputOwnership();
        ownership.Process(30, true, RoutingMode.RemoteOnly);

        ModeTransitionDecision transition = ownership.ChangeMode(RoutingMode.RemoteOnly, RoutingMode.Default);
        InputRoutingDecision physicalRelease = ownership.Process(30, false, RoutingMode.Default);

        Assert.True(transition.ReleaseRemoteAll);
        Assert.Equal(RemoteInputAction.None, physicalRelease.RemoteAction);
    }

    [Fact]
    public void SharedRoutesToBothAndBlockAllRoutesToNeither()
    {
        var ownership = new InputOwnership();

        InputRoutingDecision shared = ownership.Process(1, true, RoutingMode.Shared);
        ownership.Process(1, false, RoutingMode.Shared);
        InputRoutingDecision blocked = ownership.Process(2, true, RoutingMode.BlockAll);

        Assert.False(shared.SuppressLocal);
        Assert.Equal(RemoteInputAction.Press, shared.RemoteAction);
        Assert.True(blocked.SuppressLocal);
        Assert.Equal(RemoteInputAction.None, blocked.RemoteAction);
    }

    [Fact]
    public void KeyboardReportUsesBootFormatAndErrorRollover()
    {
        var state = new KeyboardReportState();
        state.Set(new MappedKey(0, 0x02), true);
        foreach (byte usage in new byte[] { 4, 5, 6, 7, 8, 9 })
        {
            state.Set(new MappedKey(usage, 0), true);
        }

        Assert.Equal(new byte[] { 0x02, 0, 4, 5, 6, 7, 8, 9 }, state.BuildReport());

        state.Set(new MappedKey(10, 0), true);
        Assert.Equal(new byte[] { 0x02, 0, 1, 1, 1, 1, 1, 1 }, state.BuildReport());

        state.Set(new MappedKey(10, 0), false);
        Assert.Equal(new byte[] { 0x02, 0, 4, 5, 6, 7, 8, 9 }, state.BuildReport());
    }
}
