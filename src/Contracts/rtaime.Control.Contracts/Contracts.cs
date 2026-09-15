using System.Collections.ObjectModel;
using rtaime.Core;

namespace rtaime.Control.Contracts;

public static class ControlContractVersion
{
    public static CompatibilityVersion Current { get; } = new(1, 0);

    public static bool IsSupported(CompatibilityVersion version) => version == Current;

    public static void EnsureSupported(CompatibilityVersion version)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"Unsupported Control contract version '{version}'. Supported version is '{Current}'.");
    }
}

public readonly record struct ProductionId
{
    public ProductionId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Production identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static ProductionId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct ProductionSourceId
{
    public ProductionSourceId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Production source identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static ProductionSourceId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct CommandId
{
    public CommandId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Command identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static CommandId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public sealed record ProductionSourceSpecification
{
    public ProductionSourceSpecification(ProductionSourceId sourceId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Production source name is required.", nameof(name));

        SourceId = sourceId;
        Name = name.Trim();
    }

    public ProductionSourceId SourceId { get; }
    public string Name { get; }
}

public sealed record ProductionRoutingState(ProductionSourceId PreviewSourceId, ProductionSourceId ProgramSourceId);

public sealed class ProductionSpecification
{
    private readonly ReadOnlyCollection<ProductionSourceSpecification> _sources;

    public ProductionSpecification(
        CompatibilityVersion version,
        ProductionId productionId,
        string name,
        IReadOnlyList<ProductionSourceSpecification> sources,
        ProductionRoutingState initialRouting)
    {
        ControlContractVersion.EnsureSupported(version);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Production name is required.", nameof(name));
        if (sources is null)
            throw new ArgumentNullException(nameof(sources));
        if (sources.Count == 0)
            throw new ArgumentException("A production specification requires at least one logical source.", nameof(sources));
        if (sources.Any(source => source is null))
            throw new ArgumentException("Production sources must not contain null values.", nameof(sources));

        var snapshot = sources.ToArray();
        if (snapshot.Select(source => source.SourceId).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Production source identities must be unique.", nameof(sources));

        Version = version;
        ProductionId = productionId;
        Name = name.Trim();
        _sources = Array.AsReadOnly(snapshot);
        InitialRouting = initialRouting ?? throw new ArgumentNullException(nameof(initialRouting));
    }

    public CompatibilityVersion Version { get; }
    public ProductionId ProductionId { get; }
    public string Name { get; }
    public IReadOnlyList<ProductionSourceSpecification> Sources => _sources;
    public ProductionRoutingState InitialRouting { get; }
}

public sealed record DesiredProductionState
{
    public DesiredProductionState(
        CompatibilityVersion version,
        ProductionId productionId,
        Revision basedOnAuthoritativeRevision,
        ProductionRoutingState routing)
    {
        ControlContractVersion.EnsureSupported(version);
        Version = version;
        ProductionId = productionId;
        BasedOnAuthoritativeRevision = basedOnAuthoritativeRevision;
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
    }

    public CompatibilityVersion Version { get; }
    public ProductionId ProductionId { get; }
    public Revision BasedOnAuthoritativeRevision { get; }
    public ProductionRoutingState Routing { get; }
}

public sealed record AuthoritativeProductionState
{
    public AuthoritativeProductionState(
        CompatibilityVersion version,
        ProductionId productionId,
        Revision revision,
        ProductionRoutingState routing)
    {
        ControlContractVersion.EnsureSupported(version);
        Version = version;
        ProductionId = productionId;
        Revision = revision;
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
    }

    public CompatibilityVersion Version { get; }
    public ProductionId ProductionId { get; }
    public Revision Revision { get; }
    public ProductionRoutingState Routing { get; }
}

public sealed record ControlCommandMetadata
{
    public ControlCommandMetadata(
        CompatibilityVersion version,
        CommandId commandId,
        ProductionId productionId,
        Revision expectedRevision)
    {
        ControlContractVersion.EnsureSupported(version);
        Version = version;
        CommandId = commandId;
        ProductionId = productionId;
        ExpectedRevision = expectedRevision;
    }

    public CompatibilityVersion Version { get; }
    public CommandId CommandId { get; }
    public ProductionId ProductionId { get; }
    public Revision ExpectedRevision { get; }
}

public sealed record SelectPreviewCommand
{
    public SelectPreviewCommand(ControlCommandMetadata metadata, ProductionSourceId sourceId)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        SourceId = sourceId;
    }

    public ControlCommandMetadata Metadata { get; }
    public ProductionSourceId SourceId { get; }
}

public sealed record CutProgramCommand
{
    public CutProgramCommand(ControlCommandMetadata metadata, ProductionSourceId sourceId)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        SourceId = sourceId;
    }

    public ControlCommandMetadata Metadata { get; }
    public ProductionSourceId SourceId { get; }
}

public sealed class ControlValidationReport
{
    private readonly ReadOnlyCollection<ValidationIssue> _issues;

    public ControlValidationReport(IReadOnlyList<ValidationIssue> issues)
    {
        if (issues is null)
            throw new ArgumentNullException(nameof(issues));
        if (issues.Any(issue => issue is null))
            throw new ArgumentException("Validation issues must not contain null values.", nameof(issues));

        _issues = Array.AsReadOnly(issues.ToArray());
    }

    public bool IsValid => _issues.Count == 0;
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public static ControlValidationReport Valid { get; } = new(Array.Empty<ValidationIssue>());

    public static ControlValidationReport Invalid(params ValidationIssue[] issues) =>
        issues is { Length: > 0 }
            ? new ControlValidationReport(issues)
            : throw new ArgumentException("An invalid report requires at least one issue.", nameof(issues));
}
