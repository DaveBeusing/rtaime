using System.Globalization;

namespace rtaime.Core;

/// <summary>
/// Stable, opaque identity value for architecture-wide identifiers.
/// Domain-specific contracts should wrap this value when stronger type safety is required.
/// </summary>
public readonly record struct Identity
{
    public Identity(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Identity must not be empty.", nameof(value));

        Value = value;
    }

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static Identity New() => new(Guid.NewGuid());

    public static Identity Parse(string value)
    {
        if (!TryParse(value, out var identity))
            throw new FormatException("Identity must be a non-empty GUID in D format.");

        return identity;
    }

    public static bool TryParse(string? value, out Identity identity)
    {
        if (Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty)
        {
            identity = new Identity(guid);
            return true;
        }

        identity = default;
        return false;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
