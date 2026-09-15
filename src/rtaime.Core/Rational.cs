using System.Globalization;

namespace rtaime.Core;

/// <summary>
/// Canonical rational number with a strictly positive denominator.
/// </summary>
public readonly record struct Rational
{
    public Rational(long numerator, long denominator)
    {
        if (denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must be greater than zero.");

        if (numerator == 0)
        {
            Numerator = 0;
            Denominator = 1;
            return;
        }

        var divisor = GreatestCommonDivisor(AbsoluteAsUInt64(numerator), (ulong)denominator);
        Numerator = numerator / (long)divisor;
        Denominator = denominator / (long)divisor;
    }

    public long Numerator { get; }
    public long Denominator { get; }

    public double ToDouble() => (double)Numerator / Denominator;

    public static Rational Parse(string value)
    {
        if (!TryParse(value, out var rational))
            throw new FormatException("Rational value must use the canonical numerator/denominator form.");

        return rational;
    }

    public static bool TryParse(string? value, out Rational rational)
    {
        rational = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.IndexOf('/');
        if (separator <= 0 || separator != value.LastIndexOf('/'))
            return false;

        if (!long.TryParse(value.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) ||
            !long.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) ||
            denominator <= 0)
        {
            return false;
        }

        rational = new Rational(numerator, denominator);
        return true;
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Numerator}/{Denominator}");

    private static ulong AbsoluteAsUInt64(long value) =>
        value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return left;
    }
}
