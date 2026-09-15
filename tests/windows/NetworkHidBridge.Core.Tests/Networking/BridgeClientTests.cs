using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;
using NetworkHidBridge.Core.Protocol;

namespace NetworkHidBridge.Core.Tests.Networking;

public sealed class BridgeClientTests
{
    private static readonly byte[] Psk = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
    private static readonly byte[] ServerNonce = Enumerable.Range(101, 32).Select(static value => (byte)value).ToArray();

    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    [InlineData(4, 5000)]
    [InlineData(99, 5000)]
    public void ReconnectDelayIsBoundedExponential(int attempt, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), BridgeClient.GetReconnectDelay(attempt));
    }

    [Fact]
    public async Task AuthenticatesEnablesNoDelayAndRestoresDefaultWhenServerCloses()
    {
        using var listener = StartListener(out int port);
        var modeController = new ModeController();
        TcpClient? createdClient = null;
        bool? noDelayAtConnect = null;
        var bridge = new BridgeClient(
            new BridgeClientOptions(IPAddress.Loopback, port, Psk),
            modeController,
            () => createdClient = new TcpClient());
        var connected = NewSignal();
        bridge.ConnectionChanged += (_, snapshot) =>
        {
            if (snapshot.State == ConnectionState.Connected)
            {
                noDelayAtConnect = createdClient?.NoDelay;
                connected.TrySetResult();
            }
        };

        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            await AuthenticateServerAsync(peer.GetStream(), Psk);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await bridge.ConnectOnceAsync(CancellationToken.None);
        await server;

        Assert.NotNull(createdClient);
        Assert.True(noDelayAtConnect);
        Assert.Equal(ConnectionState.Disconnected, bridge.Current.State);
        Assert.Equal(RoutingMode.Default, modeController.Mode);
        Assert.False(modeController.IsAuthenticated);
    }

    [Fact]
    public async Task RejectsInvalidServerProofWithoutEnablingRemoteModes()
    {
        using var listener = StartListener(out int port);
        var modeController = new ModeController();
        var bridge = new BridgeClient(new(IPAddress.Loopback, port, Psk), modeController);

        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            NetworkStream stream = peer.GetStream();
            Frame hello = await FrameCodec.ReadHandshakeAsync(stream, MessageType.ClientHello);
            var badProof = new byte[32];
            var payload = ServerNonce.Concat(badProof).ToArray();
            await FrameCodec.WriteHandshakeAsync(stream, new(MessageType.ServerHello, 0, payload));
        });

        await bridge.ConnectOnceAsync(CancellationToken.None);
        await server;

        Assert.Equal(ConnectionState.Disconnected, bridge.Current.State);
        Assert.Equal(ConnectionError.Authentication, bridge.Current.LastError);
        Assert.False(modeController.IsAuthenticated);
        Assert.False(modeController.TrySetMode(RoutingMode.RemoteOnly));
    }

    [Fact]
    public async Task SerializesKeyboardThenReleaseAllWhenRemoteRoutingIsDisabled()
    {
        using var listener = StartListener(out int port);
        var modeController = new ModeController();
        var bridge = new BridgeClient(new(IPAddress.Loopback, port, Psk), modeController);
        var connected = NewSignal();
        bridge.ConnectionChanged += (_, value) =>
        {
            if (value.State == ConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        byte[]? keyboardPayload = null;
        MessageType secondType = 0;

        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            byte[] sessionKey = await AuthenticateServerAsync(peer.GetStream(), Psk);
            var receiveSequence = new SequenceTracker();
            Frame keyboard = await FrameCodec.ReadAuthenticatedAsync(peer.GetStream(), sessionKey, receiveSequence);
            Frame release = await FrameCodec.ReadAuthenticatedAsync(peer.GetStream(), sessionKey, receiveSequence);
            keyboardPayload = keyboard.Payload.ToArray();
            secondType = release.MessageType;
        });

        Task client = bridge.ConnectOnceAsync(CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(modeController.TrySetMode(RoutingMode.RemoteOnly));
        bridge.SetRemoteRouting(true);
        Assert.True(bridge.QueueKeyboard(new byte[] { 2, 0, 4, 0, 0, 0, 0, 0 }));
        bridge.SetRemoteRouting(false);

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        await client.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 2, 0, 4, 0, 0, 0, 0, 0 }, keyboardPayload);
        Assert.Equal(MessageType.ReleaseAll, secondType);
    }

    [Fact]
    public async Task HeartbeatAcknowledgementUpdatesRoundTripTime()
    {
        using var listener = StartListener(out int port);
        var modeController = new ModeController();
        var bridge = new BridgeClient(new(IPAddress.Loopback, port, Psk), modeController);
        var rttUpdated = NewSignal();
        bridge.ConnectionChanged += (_, snapshot) =>
        {
            if (snapshot.RoundTripTime is not null)
            {
                rttUpdated.TrySetResult();
            }
        };

        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            NetworkStream stream = peer.GetStream();
            byte[] sessionKey = await AuthenticateServerAsync(stream, Psk);
            Frame heartbeat = await FrameCodec.ReadAuthenticatedAsync(stream, sessionKey, new SequenceTracker());
            Assert.Equal(MessageType.Heartbeat, heartbeat.MessageType);
            await FrameCodec.WriteAuthenticatedAsync(
                stream,
                MessageType.HeartbeatAck,
                heartbeat.Payload.ToArray(),
                sessionKey,
                new SequenceTracker());
            await rttUpdated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await bridge.ConnectOnceAsync(CancellationToken.None);
        await server;

        Assert.NotNull(bridge.Current.RoundTripTime);
        Assert.True(bridge.Current.RoundTripTime >= TimeSpan.Zero);
    }

    [Fact]
    public void MousePayloadUsesNetworkByteOrderAndProtocolWidths()
    {
        byte[] payload = BridgePayload.EncodeMouse(new MouseSnapshot(0x11, 0x01020304, -2, 300, -300));

        Assert.Equal(13, payload.Length);
        Assert.Equal(0x11, payload[0]);
        Assert.Equal(0x01020304, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1, 4)));
        Assert.Equal(-2, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(5, 4)));
        Assert.Equal(300, BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(9, 2)));
        Assert.Equal(-300, BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(11, 2)));
    }

    [Fact]
    public async Task CoalescesMouseMovementIntoScheduledProtocolFrame()
    {
        using var listener = StartListener(out int port);
        var modeController = new ModeController();
        var bridge = new BridgeClient(new(IPAddress.Loopback, port, Psk), modeController);
        var connected = NewSignal();
        bridge.ConnectionChanged += (_, snapshot) =>
        {
            if (snapshot.State == ConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        MouseSnapshot received = default;

        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            byte[] key = await AuthenticateServerAsync(peer.GetStream(), Psk);
            Frame frame = await FrameCodec.ReadAuthenticatedAsync(peer.GetStream(), key, new SequenceTracker());
            Assert.Equal(MessageType.MouseState, frame.MessageType);
            ReadOnlySpan<byte> payload = frame.Payload.Span;
            received = new(
                payload[0],
                BinaryPrimitives.ReadInt32BigEndian(payload.Slice(1, 4)),
                BinaryPrimitives.ReadInt32BigEndian(payload.Slice(5, 4)),
                BinaryPrimitives.ReadInt16BigEndian(payload.Slice(9, 2)),
                BinaryPrimitives.ReadInt16BigEndian(payload.Slice(11, 2)));
        });

        Task client = bridge.ConnectOnceAsync(CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(modeController.TrySetMode(RoutingMode.RemoteOnly));
        bridge.SetRemoteRouting(true);
        bridge.AddMouseMovement(10, -4);
        bridge.AddMouseMovement(5, 1);
        bridge.AddMouseWheel(120, -20);
        bridge.SetMouseButton(5, true);

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        await client.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new MouseSnapshot(0x10, 15, -3, 120, -20), received);
    }

    [Fact]
    public async Task SendsCompleteStateEveryHalfSecondWhileRemoteIsEnabled()
    {
        using var listener = StartListener(out int port);
        var modeController = new ModeController();
        var bridge = new BridgeClient(new(IPAddress.Loopback, port, Psk), modeController);
        var connected = NewSignal();
        bridge.ConnectionChanged += (_, snapshot) =>
        {
            if (snapshot.State == ConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        byte[]? fullState = null;

        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            byte[] key = await AuthenticateServerAsync(peer.GetStream(), Psk);
            var sequence = new SequenceTracker();
            while (fullState is null)
            {
                Frame frame = await FrameCodec.ReadAuthenticatedAsync(peer.GetStream(), key, sequence);
                if (frame.MessageType == MessageType.FullState)
                {
                    fullState = frame.Payload.ToArray();
                }
            }
        });

        Task client = bridge.ConnectOnceAsync(CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(modeController.TrySetMode(RoutingMode.RemoteOnly));
        bridge.SetRemoteRouting(true);
        Assert.True(bridge.QueueKeyboard(new byte[] { 2, 0, 4, 0, 0, 0, 0, 0 }));
        Assert.True(bridge.QueueConsumer(ConsumerUsage.Mute));

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        await client.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 2, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0xE2 }, fullState);
    }

    [Fact]
    public async Task CancellationDuringHandshakePropagatesAndRestoresDefault()
    {
        using var listener = StartListener(out int port);
        var modeController = new ModeController();
        var bridge = new BridgeClient(new(IPAddress.Loopback, port, Psk), modeController);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bridge.ConnectOnceAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server);

        Assert.Equal(ConnectionState.Disconnected, bridge.Current.State);
        Assert.Equal(RoutingMode.Default, modeController.Mode);
        Assert.False(modeController.IsAuthenticated);
    }

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task<byte[]> AuthenticateServerAsync(NetworkStream stream, byte[] psk)
    {
        Frame hello = await FrameCodec.ReadHandshakeAsync(stream, MessageType.ClientHello);
        byte[] clientNonce = hello.Payload.ToArray();
        byte[] proof = SessionAuthenticator.DeriveServerProof(psk, clientNonce, ServerNonce);
        byte[] payload = ServerNonce.Concat(proof).ToArray();
        await FrameCodec.WriteHandshakeAsync(stream, new(MessageType.ServerHello, 0, payload));
        Frame auth = await FrameCodec.ReadHandshakeAsync(stream, MessageType.ClientAuth);
        Assert.True(SessionAuthenticator.VerifyClientProof(auth.Payload.ToArray(), psk, clientNonce, ServerNonce));
        return SessionAuthenticator.DeriveSessionKey(psk, clientNonce, ServerNonce);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
