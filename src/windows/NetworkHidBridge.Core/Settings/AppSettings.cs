using System.Net;
using System.Security.Cryptography;

namespace NetworkHidBridge.Core.Settings;

public sealed class AppSettings
{
    private readonly byte[] _psk;

    public AppSettings(IPAddress address, int port, byte[] psk, bool startWithWindows)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(psk);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("A specific Pi address is required.", nameof(address));
        }
        if (port is < 1 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        if (psk.Length != 32)
        {
            throw new ArgumentException("PSK must be exactly 32 bytes.", nameof(psk));
        }

        Address = address;
        Port = port;
        _psk = psk.ToArray();
        StartWithWindows = startWithWindows;
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public bool StartWithWindows { get; }

    public byte[] CopyPsk() => _psk.ToArray();

    public static AppSettings CreateDefault() =>
        new(IPAddress.Parse("192.168.0.4"), 45821, RandomNumberGenerator.GetBytes(32), false);
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);
