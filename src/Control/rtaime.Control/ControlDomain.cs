using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Control;

public sealed record ControlStateSnapshot(
    DesiredProductionState Desired,
    AuthoritativeProductionState Authoritative);

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

public static class ProductionSpecificationValidator
{
    public static ControlValidationReport Validate(ProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var issues = new List<ValidationIssue>();
        var knownSources = specification.Sources.Select(source => source.SourceId).ToHashSet();

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

        foreach (var scene in specification.Scenes)
        {
            if (!knownSources.Contains(scene.Routing.PreviewSourceId))
            {
                issues.Add(new ValidationIssue(
                    "control.specification.scene_preview_source_unknown",
                    $"Scene '{scene.Name}' references an unavailable Preview source.",
                    $"scenes[{scene.SceneId}].routing.previewSourceId"));
            }

            if (!knownSources.Contains(scene.Routing.ProgramSourceId))
            {
                issues.Add(new ValidationIssue(
                    "control.specification.scene_program_source_unknown",
                    $"Scene '{scene.Name}' references an unavailable Program source.",
                    $"scenes[{scene.SceneId}].routing.programSourceId"));
            }
        }

        return new ControlValidationReport(issues);
    }
}

/// <summary>
/// Pure V1 Control-domain state transition engine. CUT and DISSOLVE use the same
/// validation and authoritative commit path; transition timing remains a Runtime concern.
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
        return ApplyCore(specification, current, command.Metadata, command.SourceId, MutationKind.SelectPreview);
    }

    public static ControlCommandResult Apply(
        ProductionSpecification specification,
        AuthoritativeProductionState current,
        CutProgramCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyCore(specification, current, command.Metadata, command.SourceId, MutationKind.ProgramTransition);
    }

    public static ControlCommandResult Apply(
        ProductionSpecification specification,
        AuthoritativeProductionState current,
        DissolveProgramCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyCore(specification, current, command.Metadata, command.SourceId, MutationKind.ProgramTransition);
    }

    public static ControlCommandResult Apply(
        ProductionSpecification specification,
        AuthoritativeProductionState current,
        ActivateSceneCommand command)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);

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

        if (command.Metadata.Version != specification.Version)
        {
            issues.Add(new ValidationIssue(
                "control.command.version_mismatch",
                "Command version does not match the production specification.",
                "command.metadata.version"));
        }

        if (command.Metadata.ProductionId != specification.ProductionId)
        {
            issues.Add(new ValidationIssue(
                "control.command.production_mismatch",
                "Command targets a different production.",
                "command.metadata.productionId"));
        }

        if (command.Metadata.ExpectedRevision != current.Revision)
        {
            issues.Add(new ValidationIssue(
                "control.command.revision_conflict",
                $"Command expected authoritative revision '{command.Metadata.ExpectedRevision}' but current revision is '{current.Revision}'.",
                "command.metadata.expectedRevision"));
        }

        var scene = specification.Scenes.FirstOrDefault(candidate => candidate.SceneId == command.SceneId);
        if (scene is null)
        {
            issues.Add(new ValidationIssue(
                "control.command.scene_unknown",
                "Command target scene is not declared by the production specification.",
                "command.sceneId"));
        }
        else
        {
            ValidateRouting(specification, scene.Routing, "scene.routing", issues);
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

        var desired = new DesiredProductionState(
            specification.Version,
            specification.ProductionId,
            current.Revision,
            scene!.Routing,
            scene.SceneId);

        var desiredIssues = new List<ValidationIssue>();
        ValidateRouting(specification, desired.Routing, "desired.routing", desiredIssues);
        if (desiredIssues.Count > 0)
            return ControlCommandResult.Rejected(current, new ControlValidationReport(desiredIssues));

        var authoritative = new AuthoritativeProductionState(
            specification.Version,
            specification.ProductionId,
            current.Revision.Next(),
            desired.Routing,
            scene.SceneId);

        return ControlCommandResult.Accepted(current, desired, authoritative);
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
            MutationKind.SelectPreview => new ProductionRoutingState(targetSourceId, current.Routing.ProgramSourceId),
            MutationKind.ProgramTransition => new ProductionRoutingState(current.Routing.PreviewSourceId, targetSourceId),
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
        ProgramTransition
    }
}
