using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Protocol;

namespace NetworkHidBridge.Core.Networking;

public static class BridgePayload
{
    public static byte[] EncodeMouse(MouseSnapshot snapshot)
    {
        if (snapshot.Wheel is < short.MinValue or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Wheel is outside the protocol range.");
        }
        if (snapshot.Pan is < short.MinValue or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Pan is outside the protocol range.");
        }

        var payload = new byte[13];
        payload[0] = snapshot.Buttons;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1, 4), snapshot.DeltaX);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(5, 4), snapshot.DeltaY);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(9, 2), (short)snapshot.Wheel);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(11, 2), (short)snapshot.Pan);
        return payload;
    }

    public static byte[] EncodeConsumer(ushort usage)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, usage);
        return payload;
    }
}

public sealed class BridgeClient
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FullStateInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MouseInterval = TimeSpan.FromMilliseconds(8);

    private readonly BridgeClientOptions _options;
    private readonly ModeController _modeController;
    private readonly Func<TcpClient> _clientFactory;
    private readonly object _sync = new();
    private readonly MouseAccumulator _mouse = new();
    private readonly byte[] _keyboard = new byte[8];
    private Channel<OutboundMessage>? _outbound;
    private CancellationTokenSource? _activeSession;
    private bool _remoteRouting;
    private ushort _consumerUsage;
    private byte _lastMouseButtons;
    private ConnectionSnapshot _current;

    public BridgeClient(
        BridgeClientOptions options,
        ModeController modeController,
        Func<TcpClient>? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modeController);
        _options = options;
        _modeController = modeController;
        _clientFactory = clientFactory ?? (static () => new TcpClient());
        _current = new(
            ConnectionState.Disconnected,
            new(options.Address, options.Port),
            ConnectionError.None,
            null);
    }

    public event EventHandler<ConnectionSnapshot>? ConnectionChanged;

    public ConnectionSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public static TimeSpan GetReconnectDelay(int attempt)
    {
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        int milliseconds = attempt switch
        {
            0 => 250,
            1 => 500,
            2 => 1000,
            3 => 2000,
            _ => 5000,
        };
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool connected = await ConnectOnceCoreAsync(cancellationToken).ConfigureAwait(false);
            if (connected)
            {
                attempt = 0;
            }

            await Task.Delay(GetReconnectDelay(attempt), cancellationToken).ConfigureAwait(false);
            if (!connected && attempt < 4)
            {
                attempt++;
            }
        }
    }

    public Task ConnectOnceAsync(CancellationToken cancellationToken) =>
        ConnectOnceCoreAsync(cancellationToken);

    public void SetRemoteRouting(bool enabled)
    {
        Channel<OutboundMessage>? channel;
        lock (_sync)
        {
            _remoteRouting = enabled;
            channel = _outbound;
            if (!enabled)
            {
                Array.Clear(_keyboard);
                _consumerUsage = 0;
                _mouse.Clear();
                _lastMouseButtons = 0;
            }
        }

        if (!enabled && channel is not null)
        {
            QueueOrAbort(channel, new(MessageType.ReleaseAll, []));
        }
    }

    public bool QueueKeyboard(byte[] report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Length != 8)
        {
            throw new ArgumentException("Keyboard report must be exactly 8 bytes.", nameof(report));
        }

        Channel<OutboundMessage>? channel;
        lock (_sync)
        {
            if (!_remoteRouting || _current.State != ConnectionState.Connected)
            {
                return false;
            }
            report.CopyTo(_keyboard, 0);
            channel = _outbound;
        }

        return channel is not null && QueueOrAbort(channel, new(MessageType.KeyboardState, report.ToArray()));
    }

    public bool QueueConsumer(ushort usage)
    {
        Channel<OutboundMessage>? channel;
        lock (_sync)
        {
            if (!_remoteRouting || _current.State != ConnectionState.Connected)
            {
                return false;
            }
            _consumerUsage = usage;
            channel = _outbound;
        }

        return channel is not null && QueueOrAbort(channel, new(MessageType.ConsumerState, BridgePayload.EncodeConsumer(usage)));
    }

    public void AddMouseMovement(long deltaX, long deltaY)
    {
        lock (_sync)
        {
            if (!_remoteRouting)
            {
                return;
            }
        }
        _mouse.AddMovement(deltaX, deltaY);
    }

    public void AddMouseWheel(long wheel, long pan)
    {
        lock (_sync)
        {
            if (!_remoteRouting)
            {
                return;
            }
        }
        _mouse.AddWheel(wheel, pan);
    }

    public void SetMouseButton(int button, bool isDown)
    {
        lock (_sync)
        {
            if (!_remoteRouting)
            {
                return;
            }
        }
        _mouse.SetButton(button, isDown);
    }

    private async Task<bool> ConnectOnceCoreAsync(CancellationToken cancellationToken)
    {
        byte[]? sessionKey = null;
        bool authenticated = false;
        TcpClient? client = null;
        ConnectionError error = ConnectionError.None;
        try
        {
            Publish(ConnectionState.Connecting, ConnectionError.None);
            client = _clientFactory();
            client.NoDelay = true;
            await client.ConnectAsync(_options.Address, _options.Port, cancellationToken).ConfigureAwait(false);
            Publish(ConnectionState.Authenticating, ConnectionError.None);
            sessionKey = await AuthenticateAsync(client.GetStream(), cancellationToken).ConfigureAwait(false);

            var channel = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                _outbound = channel;
                _activeSession = sessionCancellation;
            }
            _modeController.SetAuthenticated(true);
            authenticated = true;
            Publish(ConnectionState.Connected, ConnectionError.None);

            Task writer = WriterLoopAsync(client.GetStream(), channel.Reader, sessionKey, sessionCancellation.Token);
            Task reader = ReaderLoopAsync(client.GetStream(), sessionKey, sessionCancellation.Token);
            Task scheduler = SchedulerLoopAsync(channel, sessionCancellation.Token);
            await Task.WhenAny(writer, reader, scheduler).ConfigureAwait(false);
            sessionCancellation.Cancel();
            channel.Writer.TryComplete();
            await IgnoreSessionTerminationAsync(writer, reader, scheduler).ConfigureAwait(false);
            return true;
        }
        catch (BridgeAuthenticationException)
        {
            error = ConnectionError.Authentication;
            return false;
        }
        catch (ProtocolException)
        {
            error = ConnectionError.Protocol;
            return authenticated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            error = ConnectionError.Network;
            return authenticated;
        }
        finally
        {
            lock (_sync)
            {
                _outbound = null;
                _activeSession = null;
                _remoteRouting = false;
                Array.Clear(_keyboard);
                _consumerUsage = 0;
                _mouse.Clear();
                _lastMouseButtons = 0;
            }
            _modeController.SetAuthenticated(false);
            Publish(ConnectionState.Disconnected, error);
            client?.Dispose();
            if (sessionKey is not null)
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
        }
    }

    private async Task<byte[]> AuthenticateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] psk = _options.CopyPsk();
        byte[] clientNonce = RandomNumberGenerator.GetBytes(SessionAuthenticator.NonceLength);
        try
        {
            await FrameCodec.WriteHandshakeAsync(
                stream,
                new(MessageType.ClientHello, 0, clientNonce),
                cancellationToken).ConfigureAwait(false);
            Frame response = await FrameCodec.ReadHandshakeAsync(
                stream,
                MessageType.ServerHello,
                cancellationToken).ConfigureAwait(false);
            byte[] responsePayload = response.Payload.ToArray();
            byte[] serverNonce = responsePayload.AsSpan(0, 32).ToArray();
            byte[] proof = responsePayload.AsSpan(32, 32).ToArray();
            try
            {
                if (!SessionAuthenticator.VerifyServerProof(proof, psk, clientNonce, serverNonce))
                {
                    throw new BridgeAuthenticationException();
                }

                byte[] clientProof = SessionAuthenticator.DeriveClientProof(psk, clientNonce, serverNonce);
                try
                {
                    await FrameCodec.WriteHandshakeAsync(
                        stream,
                        new(MessageType.ClientAuth, 0, clientProof),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clientProof);
                }
                return SessionAuthenticator.DeriveSessionKey(psk, clientNonce, serverNonce);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responsePayload);
                CryptographicOperations.ZeroMemory(serverNonce);
                CryptographicOperations.ZeroMemory(proof);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
            CryptographicOperations.ZeroMemory(clientNonce);
        }
    }

    private static async Task WriterLoopAsync(
        Stream stream,
        ChannelReader<OutboundMessage> reader,
        byte[] sessionKey,
        CancellationToken cancellationToken)
    {
        var sequence = new SequenceTracker();
        await foreach (OutboundMessage message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await FrameCodec.WriteAuthenticatedAsync(
                stream,
                message.Type,
                message.Payload,
                sessionKey,
                sequence,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReaderLoopAsync(Stream stream, byte[] sessionKey, CancellationToken cancellationToken)
    {
        var sequence = new SequenceTracker();
        while (true)
        {
            Frame frame = await FrameCodec.ReadAuthenticatedAsync(
                stream,
                sessionKey,
                sequence,
                cancellationToken).ConfigureAwait(false);
            if (frame.MessageType != MessageType.HeartbeatAck)
            {
                throw new ProtocolException("Unexpected server message type.");
            }

            ulong sent = BinaryPrimitives.ReadUInt64BigEndian(frame.Payload.Span);
            long now = Stopwatch.GetTimestamp();
            if (sent <= (ulong)now)
            {
                PublishRoundTrip(Stopwatch.GetElapsedTime((long)sent, now));
            }
        }
    }

    private async Task SchedulerLoopAsync(Channel<OutboundMessage> channel, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MouseInterval);
        long lastHeartbeat = Stopwatch.GetTimestamp();
        long lastFullState = lastHeartbeat;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            long now = Stopwatch.GetTimestamp();
            bool remote;
            lock (_sync)
            {
                remote = _remoteRouting;
            }

            if (remote)
            {
                MouseSnapshot mouse = _mouse.DrainForProtocol();
                if (mouse.DeltaX != 0 || mouse.DeltaY != 0 || mouse.Wheel != 0 || mouse.Pan != 0
                    || mouse.Buttons != _lastMouseButtons)
                {
                    _lastMouseButtons = mouse.Buttons;
                    QueueOrAbort(channel, new(MessageType.MouseState, BridgePayload.EncodeMouse(mouse)));
                }

                if (Stopwatch.GetElapsedTime(lastFullState, now) >= FullStateInterval)
                {
                    QueueOrAbort(channel, new(MessageType.FullState, BuildFullState()));
                    lastFullState = now;
                }
            }
            else
            {
                lastFullState = now;
            }

            if (Stopwatch.GetElapsedTime(lastHeartbeat, now) >= HeartbeatInterval)
            {
                var payload = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(payload, (ulong)now);
                QueueOrAbort(channel, new(MessageType.Heartbeat, payload));
                lastHeartbeat = now;
            }
        }
    }

    private byte[] BuildFullState()
    {
        var payload = new byte[11];
        lock (_sync)
        {
            _keyboard.CopyTo(payload, 0);
            payload[8] = _lastMouseButtons;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(9, 2), _consumerUsage);
        }
        return payload;
    }

    private bool QueueOrAbort(Channel<OutboundMessage> channel, OutboundMessage message)
    {
        if (channel.Writer.TryWrite(message))
        {
            return true;
        }

        lock (_sync)
        {
            _activeSession?.Cancel();
        }
        return false;
    }

    private static async Task IgnoreSessionTerminationAsync(params Task[] tasks)
    {
        foreach (Task task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    private void Publish(ConnectionState state, ConnectionError error)
    {
        ConnectionSnapshot snapshot;
        lock (_sync)
        {
            snapshot = _current = _current with { State = state, LastError = error };
        }
        ConnectionChanged?.Invoke(this, snapshot);
    }

    private void PublishRoundTrip(TimeSpan roundTrip)
    {
        ConnectionSnapshot snapshot;
        lock (_sync)
        {
            snapshot = _current = _current with { RoundTripTime = roundTrip };
        }
        ConnectionChanged?.Invoke(this, snapshot);
    }

    private sealed record OutboundMessage(MessageType Type, byte[] Payload);

    private sealed class BridgeAuthenticationException : Exception
    {
    }
}
