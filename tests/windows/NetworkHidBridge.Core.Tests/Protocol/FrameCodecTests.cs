using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NetworkHidBridge.Core.Protocol;

namespace NetworkHidBridge.Core.Tests.Protocol;

public sealed class FrameCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void FrameDefensivelyCopiesPayload()
    {
        var payload = new byte[8];
        var frame = new Frame(MessageType.KeyboardState, 1, payload);

        payload[0] = 99;
        var exposed = frame.Payload.ToArray();
        exposed[1] = 99;

        Assert.Equal(new byte[8], frame.Payload.ToArray());
    }

    [Fact]
    public void FramePayloadCannotMutateBackingStorageThroughMemoryMarshal()
    {
        var frame = new Frame(MessageType.KeyboardState, 1, new byte[8]);
        Assert.True(MemoryMarshal.TryGetArray(frame.Payload, out ArraySegment<byte> exposed));

        exposed.Array![exposed.Offset] = 99;

        Assert.Equal(0, frame.Payload.Span[0]);
    }

    [Fact]
    public void HandshakeRoundTrips()
    {
        var frame = new Frame(MessageType.ClientHello, 0, Enumerable.Repeat((byte)0x41, 32).ToArray());
        AssertFrame(frame, FrameCodec.DecodeHandshake(FrameCodec.EncodeHandshake(frame), MessageType.ClientHello));
    }

    [Fact]
    public void AuthenticatedEncodingMatchesFrozenFixture()
    {
        var key = Convert.FromHexString("ddfd4bcf514438966c64721355bdd8b01a8a808927214daa4e3b3219b4e5d951");
        var frame = new Frame(MessageType.KeyboardState, 1, Convert.FromHexString("0200040000000000"));
        var expected = Convert.FromHexString(
            "4e4849440110000000000000000000010000000802000400000000004f8aa829f97fc236254a3d176abc6c63");

        Assert.Equal(expected, FrameCodec.EncodeAuthenticated(frame, key));
        AssertFrame(frame, FrameCodec.DecodeAuthenticated(expected, key, 1));
    }

    [Theory]
    [InlineData("4641494c01100000000000000000000100000008", "magic")]
    [InlineData("4e48494402100000000000000000000100000008", "version")]
    [InlineData("4e48494401100001000000000000000100000008", "flags")]
    [InlineData("4e484944017f0000000000000000000100000008", "message type")]
    [InlineData("4e48494401010000000000000000000000000020", "phase")]
    [InlineData("4e48494401100000000000000000000100000007", "payload length")]
    [InlineData("4e48494401100000000000000000000100001001", "too large")]
    public void AuthenticatedDecodeRejectsInvalidHeader(string headerHex, string error)
    {
        var encoded = Convert.FromHexString(headerHex).Concat(new byte[80]).ToArray();
        var exception = Assert.Throws<ProtocolException>(() => FrameCodec.DecodeAuthenticated(encoded, Key, 1));
        Assert.Contains(error, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void AuthenticatedDecodeRejectsMissingOrExtraTag(int suffixLength)
    {
        var encoded = Header(MessageType.ReleaseAll, 1, 0).Concat(new byte[suffixLength]).ToArray();
        Assert.Throws<ProtocolException>(() => FrameCodec.DecodeAuthenticated(encoded, Key, 1));
    }

    [Fact]
    public void AuthenticatedDecodeRejectsInvalidTagAndSequence()
    {
        var valid = FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, 1, []), Key);
        valid[^1] ^= 1;
        Assert.Contains("authentication", Assert.Throws<ProtocolException>(
            () => FrameCodec.DecodeAuthenticated(valid, Key, 1)).Message, StringComparison.OrdinalIgnoreCase);

        var skipped = FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, 3, []), Key);
        Assert.Contains("sequence", Assert.Throws<ProtocolException>(
            () => FrameCodec.DecodeAuthenticated(skipped, Key, 2)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task InvalidKeyFailsBeforeStreamIo(int keyLength)
    {
        var stream = new ObservingStream([]);
        await Assert.ThrowsAsync<ArgumentException>(
            () => FrameCodec.ReadAuthenticatedAsync(stream, new byte[keyLength], new SequenceTracker()));
        Assert.Equal(0, stream.ReadCalls);
    }

    [Fact]
    public async Task ReadsFragmentedAndConcatenatedFrames()
    {
        var first = FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, 1, []), Key);
        var second = FrameCodec.EncodeAuthenticated(new Frame(MessageType.Heartbeat, 2, "12345678"u8.ToArray()), Key);
        var stream = new FragmentingStream(first.Concat(second).ToArray(), 3);
        var tracker = new SequenceTracker();

        Assert.Equal(MessageType.ReleaseAll, (await FrameCodec.ReadAuthenticatedAsync(stream, Key, tracker)).MessageType);
        Assert.Equal(MessageType.Heartbeat, (await FrameCodec.ReadAuthenticatedAsync(stream, Key, tracker)).MessageType);
        Assert.Equal(3UL, tracker.ExpectedSequence);
    }

    [Theory]
    [MemberData(nameof(TruncatedFrames))]
    public async Task ReadRejectsEofAndTruncation(byte[] wire)
    {
        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.ReadAuthenticatedAsync(new MemoryStream(wire), Key, new SequenceTracker()));
    }

    public static TheoryData<byte[]> TruncatedFrames => new()
    {
        Array.Empty<byte>(),
        "NHI"u8.ToArray(),
        Header(MessageType.KeyboardState, 1, 8).Concat("123"u8.ToArray()).ToArray(),
        Header(MessageType.KeyboardState, 1, 8).Concat("12345678short"u8.ToArray()).ToArray(),
    };

    [Theory]
    [MemberData(nameof(RejectedHeaders))]
    public async Task InvalidHeaderIsRejectedBeforePayloadRead(byte[] invalidHeader)
    {
        var stream = new ObservingStream(invalidHeader.Concat(new byte[100]).ToArray(), maxReads: 1);
        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.ReadAuthenticatedAsync(stream, Key, new SequenceTracker()));
        Assert.Equal(1, stream.ReadCalls);
        Assert.Equal(FrameCodec.HeaderLength, stream.BytesRead);
    }

    public static TheoryData<byte[]> RejectedHeaders => new()
    {
        Header(MessageType.KeyboardState, 1, 8, magic: "FAIL"u8.ToArray()),
        Header(MessageType.KeyboardState, 1, 8, version: 2),
        Header(MessageType.KeyboardState, 1, 8, flags: 1),
        Header((MessageType)0x7f, 1, 8),
        Header(MessageType.ClientHello, 0, 32),
        Header(MessageType.KeyboardState, 1, 4097),
        Header(MessageType.KeyboardState, 1, 7),
        Header(MessageType.KeyboardState, 2, 8),
    };

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FrameCodec.ReadAuthenticatedAsync(new BlockingStream(), Key, new SequenceTracker(), cancellation.Token));
    }

    [Fact]
    public async Task TrackerAdvancesOnlyAfterValidReadAndWrite()
    {
        var receive = new SequenceTracker();
        var invalid = FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, 1, []), Key);
        invalid[^1] ^= 1;
        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.ReadAuthenticatedAsync(new MemoryStream(invalid), Key, receive));
        Assert.Equal(1UL, receive.ExpectedSequence);

        var valid = FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, 1, []), Key);
        await FrameCodec.ReadAuthenticatedAsync(new MemoryStream(valid), Key, receive);
        Assert.Equal(2UL, receive.ExpectedSequence);

        var send = new SequenceTracker();
        var destination = new MemoryStream();
        await FrameCodec.WriteAuthenticatedAsync(destination, MessageType.ReleaseAll, [], Key, send);
        Assert.Equal(2UL, send.ExpectedSequence);
        AssertFrame(new Frame(MessageType.ReleaseAll, 1, []), FrameCodec.DecodeAuthenticated(destination.ToArray(), Key, 1));
    }

    [Fact]
    public async Task FailedOrCancelledWriteDoesNotAdvanceSequence()
    {
        var tracker = new SequenceTracker();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FrameCodec.WriteAuthenticatedAsync(
            new MemoryStream(), MessageType.ReleaseAll, [], Key, tracker, cancellation.Token));
        Assert.Equal(1UL, tracker.ExpectedSequence);
    }

    [Fact]
    public void SequenceZeroIsRejectedAtAllAuthenticatedEntryPoints()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SequenceTracker(0));
        Assert.Throws<ProtocolException>(() =>
            FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, 0, []), Key));
        var valid = FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, 1, []), Key);
        Assert.Throws<ProtocolException>(() => FrameCodec.DecodeAuthenticated(valid, Key, 0));
    }

    [Fact]
    public async Task TrackerExhaustionIsExplicitBeforeIoOrMutation()
    {
        var receive = new SequenceTracker(ulong.MaxValue);
        var final = FrameCodec.EncodeAuthenticated(new Frame(MessageType.ReleaseAll, ulong.MaxValue, []), Key);
        await FrameCodec.ReadAuthenticatedAsync(new MemoryStream(final), Key, receive);
        Assert.True(receive.IsExhausted);
        var source = new ObservingStream([]);
        await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAuthenticatedAsync(source, Key, receive));
        Assert.Equal(0, source.ReadCalls);

        var destination = new ObservingStream([]);
        await Assert.ThrowsAsync<ProtocolException>(
            () => FrameCodec.WriteAuthenticatedAsync(destination, MessageType.ReleaseAll, [], Key, receive));
        Assert.Equal(0, destination.WriteCalls);
    }

    [Fact]
    public async Task HandshakeStreamRoundTripsAndRejectsWrongPhaseBeforePayload()
    {
        var frame = new Frame(MessageType.ClientAuth, 0, new byte[32]);
        var stream = new FragmentingStream(FrameCodec.EncodeHandshake(frame), 2);
        AssertFrame(frame, await FrameCodec.ReadHandshakeAsync(stream, MessageType.ClientAuth));

        var invalid = new ObservingStream(Header(MessageType.Heartbeat, 1, 8).Concat(new byte[8]).ToArray(), maxReads: 1);
        await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadHandshakeAsync(invalid));
        Assert.Equal(FrameCodec.HeaderLength, invalid.BytesRead);
    }

    private static byte[] Header(
        MessageType type,
        ulong sequence,
        uint payloadLength,
        byte[]? magic = null,
        byte version = 1,
        ushort flags = 0)
    {
        var result = new byte[FrameCodec.HeaderLength];
        (magic ?? "NHID"u8.ToArray()).CopyTo(result, 0);
        result[4] = version;
        result[5] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), payloadLength);
        return result;
    }

    private static void AssertFrame(Frame expected, Frame actual)
    {
        Assert.Equal(expected.MessageType, actual.MessageType);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());
    }

    private sealed class FragmentingStream(byte[] data, int fragmentSize) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
    }

    private sealed class ObservingStream(byte[] data, int maxReads = int.MaxValue) : MemoryStream(data)
    {
        public int ReadCalls { get; private set; }
        public int BytesRead { get; private set; }
        public int WriteCalls { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (ReadCalls > maxReads)
            {
                throw new InvalidOperationException("Unexpected payload read.");
            }
            return ReadObservedAsync(buffer, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadObservedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled<int>(cancellationToken);
    }
}
