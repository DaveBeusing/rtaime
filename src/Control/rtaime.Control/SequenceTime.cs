// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Numerics;
using rtaime.Control.Contracts;
namespace rtaime.Control;

// Authoring units, not a wall/media clock. All authority arithmetic is integral/rational.
public static class SequenceTime
{
    public static bool ValidRate(SequenceRate rate) => rate.Numerator > 0 && rate.Numerator <= 240000 &&
        rate.Denominator > 0 && rate.Denominator <= 100000 && (long)rate.Numerator <= 240L * rate.Denominator;
    public static long FromFrames(long frames, SequenceRate rate, bool ceiling = false)
    {
        if (!ValidRate(rate) || frames < 0) throw new ArgumentException("Invalid frame boundary or rational rate.");
        var n = (BigInteger)frames * SequenceContract.TicksPerSecond * rate.Denominator;
        return Divide(n, rate.Numerator, ceiling);
    }
    public static long ToFrames(long ticks, SequenceRate rate, bool ceiling = false)
    {
        if (!ValidRate(rate) || ticks < 0) throw new ArgumentException("Invalid time boundary or rational rate.");
        return Divide((BigInteger)ticks * rate.Numerator, (BigInteger)SequenceContract.TicksPerSecond * rate.Denominator, ceiling);
    }
    public static long FromTimeSpanTicks(long ticks)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        return Divide((BigInteger)ticks * SequenceContract.TicksPerSecond, TimeSpan.TicksPerSecond, false);
    }
    private static long Divide(BigInteger n, BigInteger d, bool ceiling) =>
        checked((long)((n + (ceiling ? d - 1 : 0)) / d));
}
