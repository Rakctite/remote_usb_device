using NetworkHidBridge.Core.Settings;

namespace NetworkHidBridge.Core.Tests.Settings;

public sealed class StartupTaskManagerTests
{
    [Fact]
    public void CreateArgumentsUseHighestOnLogonForCurrentUserAndExactExecutable()
    {
        IReadOnlyList<string> arguments = StartupTaskManager.BuildCreateArguments(
            @"C:\Program Files\IoTSys\NetworkHidBridge.App.exe",
            @"MACHINE\user");

        Assert.Equal(
            new[]
            {
                "/Create", "/TN", StartupTaskManager.TaskName,
                "/TR", "\"C:\\Program Files\\IoTSys\\NetworkHidBridge.App.exe\"",
                "/SC", "ONLOGON", "/RL", "HIGHEST", "/RU", @"MACHINE\user", "/F",
            },
            arguments);
    }

    [Fact]
    public void DeleteArgumentsAreDeterministic()
    {
        Assert.Equal(
            new[] { "/Delete", "/TN", StartupTaskManager.TaskName, "/F" },
            StartupTaskManager.BuildDeleteArguments());
    }

    [Fact]
    public void QueryArgumentsAreDeterministic()
    {
        Assert.Equal(
            new[] { "/Query", "/TN", StartupTaskManager.TaskName },
            StartupTaskManager.BuildQueryArguments());
    }
}
