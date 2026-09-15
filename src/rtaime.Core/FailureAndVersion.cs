using System.Globalization;

namespace rtaime.Core;

/// <summary>
/// Dependency-neutral failure representation for machine-readable error propagation.
/// </summary>
public readonly record struct Failure
{
    public Failure(string code, string message)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Failure code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Failure message is required.", nameof(message));

        Code = code.Trim();
        Message = message.Trim();
    }

    public string Code { get; }
    public string Message { get; }

    public bool IsDefined => !string.IsNullOrWhiteSpace(Code);

    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>
/// Major/minor compatibility marker. It deliberately does not encode a compatibility policy;
/// contracts decide whether a specific version can be consumed.
/// </summary>
public readonly record struct CompatibilityVersion(uint Major, uint Minor) : IComparable<CompatibilityVersion>
{
    public int CompareTo(CompatibilityVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        return majorComparison != 0 ? majorComparison : Minor.CompareTo(other.Minor);
    }

    public bool HasSameMajor(CompatibilityVersion other) => Major == other.Major;

    public static CompatibilityVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
            throw new FormatException("Compatibility version must use major.minor with unsigned integer components.");

        return version;
    }

    public static bool TryParse(string? value, out CompatibilityVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.IndexOf('.');
        if (separator <= 0 || separator != value.LastIndexOf('.'))
            return false;

        if (!uint.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !uint.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new CompatibilityVersion(major, minor);
        return true;
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Major}.{Minor}");
}
