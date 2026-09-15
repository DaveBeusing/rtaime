using System.Collections.ObjectModel;

namespace rtaime.Core;

/// <summary>
/// Stable validation issue with a machine-readable code and human-readable message.
/// </summary>
public sealed record ValidationIssue
{
    public ValidationIssue(string code, string message, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Validation issue code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Validation issue message is required.", nameof(message));

        Code = code.Trim();
        Message = message.Trim();
        Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    public string Code { get; }
    public string Message { get; }
    public string? Path { get; }
}

/// <summary>
/// Immutable validation outcome. A valid result contains no issues.
/// </summary>
public sealed class ValidationResult
{
    private readonly ReadOnlyCollection<ValidationIssue> _issues;

    private ValidationResult(IEnumerable<ValidationIssue> issues)
    {
        var snapshot = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        if (snapshot.Any(issue => issue is null))
            throw new ArgumentException("Validation issues must not contain null values.", nameof(issues));

        _issues = Array.AsReadOnly(snapshot);
    }

    public static ValidationResult Valid { get; } = new(Array.Empty<ValidationIssue>());

    public bool IsValid => _issues.Count == 0;
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public static ValidationResult Invalid(params ValidationIssue[] issues)
    {
        if (issues is null)
            throw new ArgumentNullException(nameof(issues));
        if (issues.Length == 0)
            throw new ArgumentException("An invalid validation result requires at least one issue.", nameof(issues));

        return new ValidationResult(issues);
    }

    public static ValidationResult From(IEnumerable<ValidationIssue> issues)
    {
        var snapshot = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        return snapshot.Length == 0 ? Valid : new ValidationResult(snapshot);
    }
}
