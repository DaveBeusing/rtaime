using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Control;

/// <summary>
/// Immutable pair of desired and authoritative Control state.
/// </summary>
public sealed record ControlStateSnapshot(
    DesiredProductionState Desired,
    AuthoritativeProductionState Authoritative);

/// <summary>
/// Fail-closed result of creating the initial Control state from a production specification.
/// </summary>
public sealed class ControlInitializationResult
{
    private ControlInitializationResult(ControlValidationReport validation, ControlStateSnapshot? state)
    {
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        State = state;

        if (validation.IsValid != (state is not null))
            throw new ArgumentException("A valid initialization result requires state and an invalid result must not expose state.");
    }

    public ControlValidationReport Validation { get; }
    public ControlStateSnapshot? State { get; }
    public bool Succeeded => State is not null;

    internal static ControlInitializationResult Accepted(ControlStateSnapshot state) =>
        new(ControlValidationReport.Valid, state ?? throw new ArgumentNullException(nameof(state)));

    internal static ControlInitializationResult Rejected(ControlValidationReport validation)
    {
        if (validation is null)
            throw new ArgumentNullException(nameof(validation));
        if (validation.IsValid)
            throw new ArgumentException("A rejected initialization requires validation issues.", nameof(validation));

        return new ControlInitializationResult(validation, null);
    }
}

/// <summary>
/// Result of evaluating one authoritative Control command.
/// Rejected commands return the exact current authoritative state and no desired mutation.
/// </summary>
public sealed class ControlCommandResult
{
    private ControlCommandResult(
        ControlValidationReport validation,
        AuthoritativeProductionState previousAuthoritativeState,
        DesiredProductionState? desiredState,
        AuthoritativeProductionState authoritativeState)
    {
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        PreviousAuthoritativeState = previousAuthoritativeState ?? throw new ArgumentNullException(nameof(previousAuthoritativeState));
        DesiredState = desiredState;
        AuthoritativeState = authoritativeState ?? throw new ArgumentNullException(nameof(authoritativeState));

        if (validation.IsValid && desiredState is null)
            throw new ArgumentException("A committed command requires desired state.");
        if (!validation.IsValid && desiredState is not null)
            throw new ArgumentException("A rejected command must not expose desired state.");
    }

    public ControlValidationReport Validation { get; }
    public AuthoritativeProductionState PreviousAuthoritativeState { get; }
    public DesiredProductionState? DesiredState { get; }
    public AuthoritativeProductionState AuthoritativeState { get; }
    public bool Committed => Validation.IsValid && DesiredState is not null;

    internal static ControlCommandResult Accepted(
        AuthoritativeProductionState previous,
        DesiredProductionState desired,
        AuthoritativeProductionState authoritative) =>
        new(ControlValidationReport.Valid, previous, desired, authoritative);

    internal static ControlCommandResult Rejected(
        AuthoritativeProductionState current,
        ControlValidationReport validation)
    {
        if (validation is null)
            throw new ArgumentNullException(nameof(validation));
        if (validation.IsValid)
            throw new ArgumentException("A rejected command requires validation issues.", nameof(validation));

        return new ControlCommandResult(validation, current, null, current);
    }
}

/// <summary>
/// Domain validation for the declarative V1 production specification.
/// </summary>
public static class ProductionSpecificationValidator
{
    public static ControlValidationReport Validate(ProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var issues = new List<ValidationIssue>();
        var knownSources = specification.Sources
            .Select(source => source.SourceId)
            .ToHashSet();

        if (!knownSources.Contains(specification.InitialRouting.PreviewSourceId))
        {
            issues.Add(new ValidationIssue(
                "control.specification.preview_source_unknown",
                "Initial preview source is not declared by the production specification.",
                "initialRouting.previewSourceId"));
        }

        if (!knownSources.Contains(specification.InitialRouting.ProgramSourceId))
        {
            issues.Add(new ValidationIssue(
                "control.specification.program_source_unknown",
                "Initial program source is not declared by the production specification.",
                "initialRouting.programSourceId"));
        }

        return new ControlValidationReport(issues);
    }
}

/// <summary>
/// Pure V1 Control-domain state transition engine.
/// It validates a command completely before crossing the authoritative commit boundary.
/// </summary>
public static class ControlDomainEngine
{
    public static ControlInitializationResult Initialize(ProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var validation = ProductionSpecificationValidator.Validate(specification);
        if (!validation.IsValid)
            return ControlInitializationResult.Rejected(validation);

        var authoritative = new AuthoritativeProductionState(
            specification.Version,
            specification.ProductionId,
            Revision.Initial,
            specification.InitialRouting);

        var desired = new DesiredProductionState(
            specification.Version,
            specification.ProductionId,
            authoritative.Revision,
            specification.InitialRouting);

        return ControlInitializationResult.Accepted(new ControlStateSnapshot(desired, authoritative));
    }

    public static ControlCommandResult Apply(
        ProductionSpecification specification,
        AuthoritativeProductionState current,
        SelectPreviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyCore(
            specification,
            current,
            command.Metadata,
            command.SourceId,
            MutationKind.SelectPreview);
    }

    public static ControlCommandResult Apply(
        ProductionSpecification specification,
        AuthoritativeProductionState current,
        CutProgramCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyCore(
            specification,
            current,
            command.Metadata,
            command.SourceId,
            MutationKind.CutProgram);
    }

    private static ControlCommandResult ApplyCore(
        ProductionSpecification specification,
        AuthoritativeProductionState current,
        ControlCommandMetadata metadata,
        ProductionSourceId targetSourceId,
        MutationKind mutationKind)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(metadata);

        var issues = new List<ValidationIssue>();
        issues.AddRange(ProductionSpecificationValidator.Validate(specification).Issues);

        if (current.Version != specification.Version)
        {
            issues.Add(new ValidationIssue(
                "control.state.version_mismatch",
                "Authoritative state version does not match the production specification.",
                "authoritative.version"));
        }

        if (current.ProductionId != specification.ProductionId)
        {
            issues.Add(new ValidationIssue(
                "control.state.production_mismatch",
                "Authoritative state belongs to a different production.",
                "authoritative.productionId"));
        }

        ValidateRouting(specification, current.Routing, "authoritative.routing", issues);

        if (metadata.Version != specification.Version)
        {
            issues.Add(new ValidationIssue(
                "control.command.version_mismatch",
                "Command version does not match the production specification.",
                "command.metadata.version"));
        }

        if (metadata.ProductionId != specification.ProductionId)
        {
            issues.Add(new ValidationIssue(
                "control.command.production_mismatch",
                "Command targets a different production.",
                "command.metadata.productionId"));
        }

        if (metadata.ExpectedRevision != current.Revision)
        {
            issues.Add(new ValidationIssue(
                "control.command.revision_conflict",
                $"Command expected authoritative revision '{metadata.ExpectedRevision}' but current revision is '{current.Revision}'.",
                "command.metadata.expectedRevision"));
        }

        if (!ContainsSource(specification, targetSourceId))
        {
            issues.Add(new ValidationIssue(
                "control.command.source_unknown",
                "Command target source is not declared by the production specification.",
                "command.sourceId"));
        }

        if (current.Revision.Value == ulong.MaxValue)
        {
            issues.Add(new ValidationIssue(
                "control.state.revision_exhausted",
                "Authoritative revision cannot advance beyond UInt64.MaxValue.",
                "authoritative.revision"));
        }

        if (issues.Count > 0)
            return ControlCommandResult.Rejected(current, new ControlValidationReport(issues));

        var desiredRouting = mutationKind switch
        {
            MutationKind.SelectPreview => new ProductionRoutingState(
                targetSourceId,
                current.Routing.ProgramSourceId),
            MutationKind.CutProgram => new ProductionRoutingState(
                current.Routing.PreviewSourceId,
                targetSourceId),
            _ => throw new InvalidOperationException($"Unsupported mutation kind '{mutationKind}'.")
        };

        var desired = new DesiredProductionState(
            specification.Version,
            specification.ProductionId,
            current.Revision,
            desiredRouting);

        var desiredIssues = new List<ValidationIssue>();
        ValidateRouting(specification, desired.Routing, "desired.routing", desiredIssues);
        if (desiredIssues.Count > 0)
            return ControlCommandResult.Rejected(current, new ControlValidationReport(desiredIssues));

        var authoritative = new AuthoritativeProductionState(
            specification.Version,
            specification.ProductionId,
            current.Revision.Next(),
            desired.Routing);

        return ControlCommandResult.Accepted(current, desired, authoritative);
    }

    private static void ValidateRouting(
        ProductionSpecification specification,
        ProductionRoutingState routing,
        string pathPrefix,
        ICollection<ValidationIssue> issues)
    {
        if (!ContainsSource(specification, routing.PreviewSourceId))
        {
            issues.Add(new ValidationIssue(
                "control.state.preview_source_unknown",
                "Preview source is not declared by the production specification.",
                $"{pathPrefix}.previewSourceId"));
        }

        if (!ContainsSource(specification, routing.ProgramSourceId))
        {
            issues.Add(new ValidationIssue(
                "control.state.program_source_unknown",
                "Program source is not declared by the production specification.",
                $"{pathPrefix}.programSourceId"));
        }
    }

    private static bool ContainsSource(ProductionSpecification specification, ProductionSourceId sourceId) =>
        specification.Sources.Any(source => source.SourceId == sourceId);

    private enum MutationKind
    {
        SelectPreview,
        CutProgram
    }
}
