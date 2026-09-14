using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NetworkHidBridge.Core.Protocol;

public static class FrameCodec
{
    public const int HeaderLength = 20;
    public const int TagLength = 16;
    public const int MaximumPayloadLength = 4096;

    private static ReadOnlySpan<byte> Magic => "NHID"u8;

    public static byte[] EncodeHandshake(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Sequence != 0)
        {
            throw new ProtocolException("Handshake sequence must be zero.");
        }

        var header = EncodeHeader(frame, handshake: true);
        var encoded = new byte[header.Length + frame.PayloadSpan.Length];
        header.CopyTo(encoded, 0);
        frame.PayloadSpan.CopyTo(encoded.AsSpan(header.Length));
        return encoded;
    }

    public static Frame DecodeHandshake(byte[] encoded, MessageType? expectedType = null)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length < HeaderLength)
        {
            throw new ProtocolException("Truncated frame header.");
        }

        var header = ValidateHeader(encoded.AsSpan(0, HeaderLength));
        ValidatePhase(header.MessageType, handshake: true);
        ValidateHandshakeHeader(header, expectedType);
        if (encoded.Length != HeaderLength + header.PayloadLength)
        {
            throw new ProtocolException("Invalid handshake frame length.");
        }

        return new Frame(header.MessageType, header.Sequence, encoded.AsSpan(HeaderLength).ToArray());
    }

    public static async Task<Frame> ReadHandshakeAsync(
        Stream stream,
        MessageType? expectedType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var encodedHeader = await ReadExactlyAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false);
        var header = ValidateHeader(encodedHeader);
        ValidatePhase(header.MessageType, handshake: true);
        ValidateHandshakeHeader(header, expectedType);
        var payload = await ReadExactlyAsync(stream, header.PayloadLength, cancellationToken).ConfigureAwait(false);
        return new Frame(header.MessageType, header.Sequence, payload);
    }

    public static async Task WriteHandshakeAsync(
        Stream stream,
        Frame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var encoded = EncodeHandshake(frame);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] EncodeAuthenticated(Frame frame, byte[] sessionKey)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateSessionKey(sessionKey);
        if (frame.Sequence == 0)
        {
            throw new ProtocolException("Authenticated sequence must be nonzero.");
        }

        var header = EncodeHeader(frame, handshake: false);
        var bodyLength = header.Length + frame.PayloadSpan.Length;
        var encoded = new byte[bodyLength + TagLength];
        header.CopyTo(encoded, 0);
        frame.PayloadSpan.CopyTo(encoded.AsSpan(header.Length));
        var fullTag = HMACSHA256.HashData(sessionKey, encoded.AsSpan(0, bodyLength));
        fullTag.AsSpan(0, TagLength).CopyTo(encoded.AsSpan(bodyLength));
        CryptographicOperations.ZeroMemory(fullTag);
        return encoded;
    }

    public static Frame DecodeAuthenticated(byte[] encoded, byte[] sessionKey, ulong expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ValidateSessionKey(sessionKey);
        if (encoded.Length < HeaderLength)
        {
            throw new ProtocolException("Truncated frame header.");
        }

        var header = ValidateHeader(encoded.AsSpan(0, HeaderLength));
        ValidatePhase(header.MessageType, handshake: false);
        var expectedLength = HeaderLength + header.PayloadLength + TagLength;
        if (encoded.Length != expectedLength)
        {
            throw new ProtocolException("Invalid authenticated frame length.");
        }

        ValidateExpectedSequence(expectedSequence, header.Sequence);
        var bodyLength = expectedLength - TagLength;
        var fullTag = HMACSHA256.HashData(sessionKey, encoded.AsSpan(0, bodyLength));
        var validTag = CryptographicOperations.FixedTimeEquals(
            encoded.AsSpan(bodyLength, TagLength), fullTag.AsSpan(0, TagLength));
        CryptographicOperations.ZeroMemory(fullTag);
        if (!validTag)
        {
            throw new ProtocolException("Authentication tag mismatch.");
        }

        return new Frame(header.MessageType, header.Sequence,
            encoded.AsSpan(HeaderLength, header.PayloadLength).ToArray());
    }

    public static async Task<Frame> ReadAuthenticatedAsync(
        Stream stream,
        byte[] sessionKey,
        SequenceTracker sequenceTracker,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionKey(sessionKey);
        ArgumentNullException.ThrowIfNull(sequenceTracker);
        var expectedSequence = sequenceTracker.ExpectedSequence;
        ArgumentNullException.ThrowIfNull(stream);

        var encodedHeader = await ReadExactlyAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false);
        var header = ValidateHeader(encodedHeader);
        ValidatePhase(header.MessageType, handshake: false);
        ValidateExpectedSequence(expectedSequence, header.Sequence);
        var payload = await ReadExactlyAsync(stream, header.PayloadLength, cancellationToken).ConfigureAwait(false);
        var tag = await ReadExactlyAsync(stream, TagLength, cancellationToken).ConfigureAwait(false);
        var encoded = new byte[HeaderLength + payload.Length + TagLength];
        encodedHeader.CopyTo(encoded, 0);
        payload.CopyTo(encoded, HeaderLength);
        tag.CopyTo(encoded, HeaderLength + payload.Length);
        var frame = DecodeAuthenticated(encoded, sessionKey, expectedSequence);
        sequenceTracker.Advance(expectedSequence);
        return frame;
    }

    public static async Task WriteAuthenticatedAsync(
        Stream stream,
        MessageType messageType,
        byte[] payload,
        byte[] sessionKey,
        SequenceTracker sequenceTracker,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionKey(sessionKey);
        ArgumentNullException.ThrowIfNull(sequenceTracker);
        var sequence = sequenceTracker.ExpectedSequence;
        ArgumentNullException.ThrowIfNull(stream);
        var encoded = EncodeAuthenticated(new Frame(messageType, sequence, payload), sessionKey);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        sequenceTracker.Advance(sequence);
    }

    private static byte[] EncodeHeader(Frame frame, bool handshake)
    {
        ValidateKnownType(frame.MessageType);
        ValidatePhase(frame.MessageType, handshake);
        ValidatePayloadLength(frame.MessageType, frame.PayloadSpan.Length);
        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = 1;
        header[5] = (byte)frame.MessageType;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), frame.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), (uint)frame.PayloadSpan.Length);
        return header;
    }

    private static Header ValidateHeader(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != HeaderLength)
        {
            throw new ProtocolException("Invalid header length.");
        }
        if (!encoded[..4].SequenceEqual(Magic))
        {
            throw new ProtocolException("Invalid magic.");
        }
        if (encoded[4] != 1)
        {
            throw new ProtocolException("Unsupported version.");
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(6, 2)) != 0)
        {
            throw new ProtocolException("Nonzero flags.");
        }

        var messageType = (MessageType)encoded[5];
        ValidateKnownType(messageType);
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(16, 4));
        if (payloadLength > MaximumPayloadLength)
        {
            throw new ProtocolException("Payload too large.");
        }
        ValidatePayloadLength(messageType, checked((int)payloadLength));
        return new Header(messageType, BinaryPrimitives.ReadUInt64BigEndian(encoded.Slice(8, 8)),
            checked((int)payloadLength));
    }

    private static void ValidateKnownType(MessageType messageType)
    {
        if (messageType is not (MessageType.ClientHello or MessageType.ServerHello or MessageType.ClientAuth
            or MessageType.KeyboardState or MessageType.MouseState or MessageType.ConsumerState
            or MessageType.FullState or MessageType.ReleaseAll or MessageType.Heartbeat or MessageType.HeartbeatAck))
        {
            throw new ProtocolException("Unknown message type.");
        }
    }

    private static void ValidatePhase(MessageType messageType, bool handshake)
    {
        var isHandshake = messageType is MessageType.ClientHello or MessageType.ServerHello or MessageType.ClientAuth;
        if (isHandshake != handshake)
        {
            throw new ProtocolException("Message type is invalid in this protocol phase.");
        }
    }

    private static void ValidatePayloadLength(MessageType messageType, int payloadLength)
    {
        if (payloadLength > MaximumPayloadLength)
        {
            throw new ProtocolException("Payload too large.");
        }
        var expected = messageType switch
        {
            MessageType.ClientHello => 32,
            MessageType.ServerHello => 64,
            MessageType.ClientAuth => 32,
            MessageType.KeyboardState => 8,
            MessageType.MouseState => 13,
            MessageType.ConsumerState => 2,
            MessageType.FullState => 11,
            MessageType.ReleaseAll => 0,
            MessageType.Heartbeat => 8,
            MessageType.HeartbeatAck => 8,
            _ => throw new ProtocolException("Unknown message type."),
        };
        if (payloadLength != expected)
        {
            throw new ProtocolException("Invalid payload length for message type.");
        }
    }

    private static void ValidateHandshakeHeader(Header header, MessageType? expectedType)
    {
        if (expectedType is not null && header.MessageType != expectedType)
        {
            throw new ProtocolException("Unexpected handshake message type.");
        }
        if (header.Sequence != 0)
        {
            throw new ProtocolException("Handshake sequence must be zero.");
        }
    }

    private static void ValidateExpectedSequence(ulong expectedSequence, ulong actualSequence)
    {
        if (expectedSequence == 0)
        {
            throw new ProtocolException("Expected sequence is outside the authenticated range.");
        }
        if (actualSequence != expectedSequence)
        {
            throw new ProtocolException("Unexpected sequence.");
        }
    }

    private static void ValidateSessionKey(byte[] sessionKey)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (sessionKey.Length != SessionAuthenticator.KeyLength)
        {
            throw new ArgumentException("Session key must be exactly 32 bytes.", nameof(sessionKey));
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var result = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ProtocolException("Truncated frame.", exception);
        }
        return result;
    }

    private readonly record struct Header(MessageType MessageType, ulong Sequence, int PayloadLength);
}
