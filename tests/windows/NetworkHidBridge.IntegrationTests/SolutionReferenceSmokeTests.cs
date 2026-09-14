using System.Reflection;

namespace NetworkHidBridge.IntegrationTests;

public sealed class SolutionReferenceSmokeTests
{
    [Fact]
    public void IntegrationTestsCanLoadCoreAssembly()
    {
        Assert.Equal("NetworkHidBridge.Core", Assembly.Load("NetworkHidBridge.Core").GetName().Name);
    }
}
