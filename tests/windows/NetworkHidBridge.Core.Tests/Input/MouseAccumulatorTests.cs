using NetworkHidBridge.Core.Input;

namespace NetworkHidBridge.Core.Tests.Input;

public sealed class MouseAccumulatorTests
{
    [Fact]
    public void DrainReturnsAccumulatedMovementWheelsAndButtonsThenClearsDeltas()
    {
        var accumulator = new MouseAccumulator();
        accumulator.AddMovement(12, -8);
        accumulator.AddMovement(3, 2);
        accumulator.AddWheel(120, -40);
        accumulator.SetButton(1, true);
        accumulator.SetButton(5, true);

        MouseSnapshot first = accumulator.Drain();
        MouseSnapshot second = accumulator.Drain();

        Assert.Equal(new MouseSnapshot(0b1_0001, 15, -6, 120, -40), first);
        Assert.Equal(new MouseSnapshot(0b1_0001, 0, 0, 0, 0), second);
    }

    [Fact]
    public void RejectsButtonOutsideOneThroughFive()
    {
        var accumulator = new MouseAccumulator();

        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.SetButton(0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.SetButton(6, true));
    }

    [Fact]
    public void PreservesRemainderWhenAccumulationExceedsProtocolIntegerRange()
    {
        var accumulator = new MouseAccumulator();
        accumulator.AddMovement(long.MaxValue, long.MinValue);

        MouseSnapshot first = accumulator.Drain();
        MouseSnapshot second = accumulator.Drain();

        Assert.Equal(int.MaxValue, first.DeltaX);
        Assert.Equal(int.MinValue, first.DeltaY);
        Assert.NotEqual(0, second.DeltaX);
        Assert.NotEqual(0, second.DeltaY);
    }
}
