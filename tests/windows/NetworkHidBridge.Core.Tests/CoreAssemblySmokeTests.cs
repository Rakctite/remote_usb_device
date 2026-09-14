using System.Reflection;

namespace NetworkHidBridge.Core.Tests;

public sealed class CoreAssemblySmokeTests
{
    [Fact]
    public void CoreAssemblyExposesComponentIdentity()
    {
        var coreAssembly = Assembly.Load("NetworkHidBridge.Core");
        var componentInfo = coreAssembly.GetType("NetworkHidBridge.Core.ComponentInfo");

        Assert.NotNull(componentInfo);
        Assert.Equal(
            "Windows network HID bridge core",
            componentInfo.GetProperty("Name")?.GetValue(null));
    }
}
