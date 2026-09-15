using System.Globalization;

namespace rtaime.Core;

/// <summary>
/// Monotonic revision of authoritative or contract state.
/// </summary>
public readonly record struct Revision(ulong Value) : IComparable<Revision>
{
    public static Revision Initial => new(0);

    public Revision Next() => Value == ulong.MaxValue
        ? throw new InvalidOperationException("Revision cannot advance beyond UInt64.MaxValue.")
        : new Revision(Value + 1);

    public int CompareTo(Revision other) => Value.CompareTo(other.Value);

    public static Revision Parse(string value) =>
        new(ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture));

    public static bool TryParse(string? value, out Revision revision)
    {
        var success = ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed);
        revision = success ? new Revision(parsed) : default;
        return success;
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Monotonic generation counter for replaceable resources or snapshots.
/// </summary>
public readonly record struct Generation(ulong Value) : IComparable<Generation>
{
    public static Generation Initial => new(0);

    public Generation Next() => Value == ulong.MaxValue
        ? throw new InvalidOperationException("Generation cannot advance beyond UInt64.MaxValue.")
        : new Generation(Value + 1);

    public int CompareTo(Generation other) => Value.CompareTo(other.Value);

    public static Generation Parse(string value) =>
        new(ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture));

    public static bool TryParse(string? value, out Generation generation)
    {
        var success = ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed);
        generation = success ? new Generation(parsed) : default;
        return success;
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
