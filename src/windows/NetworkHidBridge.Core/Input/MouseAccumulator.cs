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
        DrainBounded(ref _deltaX, int.MinValue, int.MaxValue),
        DrainBounded(ref _deltaY, int.MinValue, int.MaxValue),
        DrainBounded(ref _wheel, int.MinValue, int.MaxValue),
        DrainBounded(ref _pan, int.MinValue, int.MaxValue));

    public MouseSnapshot DrainForProtocol() => new(
        (byte)Volatile.Read(ref _buttons),
        DrainBounded(ref _deltaX, int.MinValue, int.MaxValue),
        DrainBounded(ref _deltaY, int.MinValue, int.MaxValue),
        DrainBounded(ref _wheel, short.MinValue, short.MaxValue),
        DrainBounded(ref _pan, short.MinValue, short.MaxValue));

    public void Clear()
    {
        Interlocked.Exchange(ref _deltaX, 0);
        Interlocked.Exchange(ref _deltaY, 0);
        Interlocked.Exchange(ref _wheel, 0);
        Interlocked.Exchange(ref _pan, 0);
        Interlocked.Exchange(ref _buttons, 0);
    }

    private static int DrainBounded(ref long accumulator, int minimum, int maximum)
    {
        long value = Interlocked.Exchange(ref accumulator, 0);
        int delivered = value switch
        {
            > 0 when value > maximum => maximum,
            < 0 when value < minimum => minimum,
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
