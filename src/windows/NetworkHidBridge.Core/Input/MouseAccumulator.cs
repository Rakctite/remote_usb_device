using System.Threading;

namespace NetworkHidBridge.Core.Input;

public readonly record struct MouseSnapshot(byte Buttons, int DeltaX, int DeltaY, int Wheel, int Pan);

public sealed class MouseAccumulator
{
    private long _deltaX;
    private long _deltaY;
    private long _wheel;
    private long _pan;
    private int _buttons;

    public void AddMovement(long deltaX, long deltaY)
    {
        Interlocked.Add(ref _deltaX, deltaX);
        Interlocked.Add(ref _deltaY, deltaY);
    }

    public void AddWheel(long wheel, long pan)
    {
        Interlocked.Add(ref _wheel, wheel);
        Interlocked.Add(ref _pan, pan);
    }

    public void SetButton(int button, bool isDown)
    {
        if (button is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(button));
        }

        int mask = 1 << (button - 1);
        int before;
        int after;
        do
        {
            before = Volatile.Read(ref _buttons);
            after = isDown ? before | mask : before & ~mask;
        }
        while (Interlocked.CompareExchange(ref _buttons, after, before) != before);
    }

    public MouseSnapshot Drain() => new(
        (byte)Volatile.Read(ref _buttons),
        DrainInt32(ref _deltaX),
        DrainInt32(ref _deltaY),
        DrainInt32(ref _wheel),
        DrainInt32(ref _pan));

    private static int DrainInt32(ref long accumulator)
    {
        long value = Interlocked.Exchange(ref accumulator, 0);
        int delivered = value switch
        {
            > int.MaxValue => int.MaxValue,
            < int.MinValue => int.MinValue,
            _ => (int)value,
        };

        long remainder = value - delivered;
        if (remainder != 0)
        {
            Interlocked.Add(ref accumulator, remainder);
        }

        return delivered;
    }
}
