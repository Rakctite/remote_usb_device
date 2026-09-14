namespace NetworkHidBridge.Core.Protocol;

public sealed class Frame
{
    private readonly byte[] _payload;

    public Frame(MessageType messageType, ulong sequence, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        MessageType = messageType;
        Sequence = sequence;
        _payload = payload.ToArray();
    }

    public MessageType MessageType { get; }

    public ulong Sequence { get; }

    public ReadOnlyMemory<byte> Payload => _payload.ToArray();

    internal ReadOnlySpan<byte> PayloadSpan => _payload;
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SequenceTracker
{
    private ulong? _expectedSequence;

    public SequenceTracker(ulong expectedSequence = 1)
    {
        if (expectedSequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSequence), "Expected sequence must be nonzero.");
        }

        _expectedSequence = expectedSequence;
    }

    public bool IsExhausted => _expectedSequence is null;

    public ulong ExpectedSequence => _expectedSequence ?? throw new ProtocolException("Sequence space exhausted.");

    internal void Advance(ulong sequence)
    {
        if (_expectedSequence != sequence)
        {
            throw new InvalidOperationException("Sequence tracker was advanced out of order.");
        }

        _expectedSequence = sequence == ulong.MaxValue ? null : sequence + 1;
    }
}
