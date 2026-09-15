using System.Globalization;

namespace rtaime.Core;

/// <summary>
/// UTC-normalized wall-clock timestamp. Formatting is always round-trip invariant culture.
/// </summary>
public readonly record struct UtcTimestamp : IComparable<UtcTimestamp>
{
    public UtcTimestamp(DateTimeOffset value)
    {
        Value = value.ToUniversalTime();
    }

    public DateTimeOffset Value { get; }

    public static UtcTimestamp UnixEpoch => new(DateTimeOffset.UnixEpoch);

    public static UtcTimestamp FromUnixTimeMilliseconds(long milliseconds) =>
        new(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));

    public long ToUnixTimeMilliseconds() => Value.ToUnixTimeMilliseconds();

    public int CompareTo(UtcTimestamp other) => Value.CompareTo(other.Value);

    public static UtcTimestamp Parse(string value)
    {
        if (!TryParse(value, out var timestamp))
            throw new FormatException("Timestamp must be a valid round-trip DateTimeOffset value.");

        return timestamp;
    }

    public static bool TryParse(string? value, out UtcTimestamp timestamp)
    {
        var success = DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed);

        timestamp = success ? new UtcTimestamp(parsed) : default;
        return success;
    }

    public override string ToString() => Value.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>
/// Signed duration expressed in .NET ticks (100 nanoseconds per tick).
/// </summary>
public readonly record struct Duration(long Ticks) : IComparable<Duration>
{
    public static Duration Zero => new(0);

    public static Duration FromTimeSpan(TimeSpan value) => new(value.Ticks);

    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks(Ticks);

    public int CompareTo(Duration other) => Ticks.CompareTo(other.Ticks);

    public static Duration Parse(string value) =>
        new(long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));

    public static bool TryParse(string? value, out Duration duration)
    {
        var success = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed);
        duration = success ? new Duration(parsed) : default;
        return success;
    }

    public override string ToString() => Ticks.ToString(CultureInfo.InvariantCulture);
}
