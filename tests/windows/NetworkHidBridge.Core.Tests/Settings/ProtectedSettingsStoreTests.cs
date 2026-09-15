using System.Net;
using System.Runtime.Versioning;
using NetworkHidBridge.Core.Settings;

namespace NetworkHidBridge.Core.Tests.Settings;

[SupportedOSPlatform("windows")]
public sealed class ProtectedSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nhid-{Guid.NewGuid():N}");

    [Fact]
    public void DefaultSettingsUseManualPiEndpointAndRandomPsk()
    {
        AppSettings settings = AppSettings.CreateDefault();

        Assert.Equal(IPAddress.Parse("192.168.0.4"), settings.Address);
        Assert.Equal(45821, settings.Port);
        Assert.Equal(32, settings.CopyPsk().Length);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public void RejectsWildcardAddressInvalidPortAndWrongPskLength()
    {
        Assert.Throws<ArgumentException>(() => new AppSettings(IPAddress.Any, 45821, new byte[32], false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSettings(IPAddress.Loopback, 0, new byte[32], false));
        Assert.Throws<ArgumentException>(() => new AppSettings(IPAddress.Loopback, 45821, new byte[31], false));
    }

    [Fact]
    public void SaveAndLoadRoundTripWithoutPlaintextPsk()
    {
        string path = Path.Combine(_directory, "settings.json");
        var store = new ProtectedSettingsStore(path);
        byte[] psk = Enumerable.Repeat((byte)'A', 32).ToArray();
        var expected = new AppSettings(IPAddress.Parse("10.1.2.3"), 50000, psk, true);

        store.Save(expected);
        SettingsLoadResult result = store.LoadOrCreate();
        string json = File.ReadAllText(path);

        Assert.Null(result.Warning);
        Assert.Equal(expected.Address, result.Settings.Address);
        Assert.Equal(expected.Port, result.Settings.Port);
        Assert.Equal(expected.StartWithWindows, result.Settings.StartWithWindows);
        Assert.Equal(psk, result.Settings.CopyPsk());
        Assert.DoesNotContain(new string('A', 32), json, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void CorruptFileReturnsFreshDefaultsAndWarning()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{not-json");
        var store = new ProtectedSettingsStore(path);

        SettingsLoadResult result = store.LoadOrCreate();

        Assert.NotNull(result.Warning);
        Assert.Equal(IPAddress.Parse("192.168.0.4"), result.Settings.Address);
        Assert.Equal(32, result.Settings.CopyPsk().Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
