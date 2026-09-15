using System.Net;

namespace NetworkHidBridge.Core.Networking;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
}

public enum ConnectionError
{
    None,
    Network,
    Authentication,
    Protocol,
}

public sealed class BridgeClientOptions
{
    private readonly byte[] _psk;

    public BridgeClientOptions(IPAddress address, int port, byte[] psk)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(psk);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
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
    }

    public IPAddress Address { get; }

    public int Port { get; }

    internal byte[] CopyPsk() => _psk.ToArray();
}

public sealed record ConnectionSnapshot(
    ConnectionState State,
    IPEndPoint Endpoint,
    ConnectionError LastError,
    TimeSpan? RoundTripTime);
