namespace rtaime.Core;

/// <summary>
/// Canonical frames-per-second value represented as an exact rational number.
/// </summary>
public readonly record struct FrameRate
{
    private readonly Rational _value;

    public FrameRate(long numerator, long denominator)
    {
        if (numerator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "Frame-rate numerator must be greater than zero.");

        _value = new Rational(numerator, denominator);
    }

    public long Numerator => _value.Numerator;
    public long Denominator => _value.Denominator;
    public double FramesPerSecond => _value.ToDouble();

    public static FrameRate Fps50 => new(50, 1);
    public static FrameRate Fps59_94 => new(60_000, 1_001);

    public static FrameRate Parse(string value)
    {
        if (!TryParse(value, out var frameRate))
            throw new FormatException("Frame rate must use a positive numerator/denominator form.");

        return frameRate;
    }

    public static bool TryParse(string? value, out FrameRate frameRate)
    {
        frameRate = default;
        if (!Rational.TryParse(value, out var rational) || rational.Numerator <= 0)
            return false;

        frameRate = new FrameRate(rational.Numerator, rational.Denominator);
        return true;
    }

    public override string ToString() => _value.ToString();
}

/// <summary>
/// Media-clock timebase expressed as exact seconds per clock tick.
/// Example: 1/90000 means one tick equals 1/90000 second.
/// </summary>
public readonly record struct Timebase
{
    private readonly Rational _secondsPerTick;

    public Timebase(long numerator, long denominator)
    {
        if (numerator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "Timebase numerator must be greater than zero.");

        _secondsPerTick = new Rational(numerator, denominator);
    }

    public long Numerator => _secondsPerTick.Numerator;
    public long Denominator => _secondsPerTick.Denominator;
    public double SecondsPerTick => _secondsPerTick.ToDouble();

    public static Timebase Parse(string value)
    {
        if (!TryParse(value, out var timebase))
            throw new FormatException("Timebase must use a positive numerator/denominator form.");

        return timebase;
    }

    public static bool TryParse(string? value, out Timebase timebase)
    {
        timebase = default;
        if (!Rational.TryParse(value, out var rational) || rational.Numerator <= 0)
            return false;

        timebase = new Timebase(rational.Numerator, rational.Denominator);
        return true;
    }

    public override string ToString() => _secondsPerTick.ToString();
}
