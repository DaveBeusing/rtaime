using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public interface IControlHostClock
{
    UtcTimestamp GetUtcNow();
}

public sealed class SystemControlHostClock : IControlHostClock
{
    public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UtcNow);
}

public sealed record ControlHostExecutionPackage(
    AuthoritativeProductionState AuthoritativeState,
    LogicalProductionGraph Graph,
    PreparedExecutionContract PreparedExecution,
    MediaSinkId ProgramSinkId,
    RuntimeProgramTransitionIntent? ProgramTransition);

public sealed record ControlHostOperationResult(
    bool Accepted,
    AuthoritativeProductionState State,
    ControlHostExecutionPackage? Execution,
    Failure? Failure)
{
    public static ControlHostOperationResult Success(ControlHostExecutionPackage execution) =>
        new(true, execution.AuthoritativeState, execution, null);

    public static ControlHostOperationResult Rejected(AuthoritativeProductionState state, Failure failure) =>
        new(false, state, null, failure);
}

/// <summary>
/// Authoritative V1 ControlHost composition root. It owns validation/state/planning, but never calls RuntimeHost.
/// Prepared execution packages cross the host boundary through Runtime contracts.
/// </summary>
public sealed class ControlHostService
{
    private readonly object _gate = new();
    private readonly ProductionSpecification _specification;
    private readonly SnapshotProviderRegistry _providers;
    private readonly BoundedProductionJournal _journal;
    private readonly IControlHostClock _clock;
    private AuthoritativeProductionState? _authoritative;

    public ControlHostService(
        ProductionSpecification specification,
        IReadOnlyList<ProviderDescriptor> providers,
        BoundedProductionJournal journal,
        IControlHostClock? clock = null)
    {
        _specification = specification ?? throw new ArgumentNullException(nameof(specification));
        _providers = new SnapshotProviderRegistry(providers ?? throw new ArgumentNullException(nameof(providers)));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? new SystemControlHostClock();
    }

    public ProductionSpecification Specification => _specification;

    public AuthoritativeProductionState State
    {
        get
        {
            lock (_gate)
                return _authoritative ?? throw new InvalidOperationException("ControlHost is not initialized.");
        }
    }

    public ControlHostOperationResult Initialize()
    {
        lock (_gate)
        {
            if (_authoritative is not null)
                throw new InvalidOperationException("ControlHost has already been initialized.");

            var initialized = ControlDomainEngine.Initialize(_specification);
            if (!initialized.Succeeded)
            {
                var failure = FromValidation("control.initialize.rejected", initialized.Validation);
                Journal(Revision.Initial, "control", "control.initialize.rejected", failure.Message, null, failure);
                throw new InvalidOperationException(failure.Message);
            }

            var proposed = initialized.State!.Authoritative;
            var planning = CapabilityPlanningEngine.Plan(_specification, proposed, _providers);
            if (!planning.Succeeded)
            {
                var failure = FromValidation("control.initialize.planning_rejected", planning.Validation);
                Journal(proposed.Revision, "planning", "planning.rejected", failure.Message, null, failure);
                throw new InvalidOperationException(failure.Message);
            }

            _authoritative = proposed;
            var execution = Package(proposed, planning, null);
            Journal(proposed.Revision, "control", "control.initialized", "Initial authoritative state and execution plan prepared.", null, null);
            Journal(proposed.Revision, "planning", "planning.prepared", execution.PreparedExecution.PreparedExecutionId.ToString(), null, null);
            return ControlHostOperationResult.Success(execution);
        }
    }

    public ControlHostOperationResult SelectPreview(SelectPreviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
            return Apply(ControlDomainEngine.Apply(_specification, Current(), command), command.Metadata.CommandId.Value, null);
    }

    public ControlHostOperationResult CutProgram(CutProgramCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            var current = Current();
            var transition = current.Routing.ProgramSourceId == command.SourceId
                ? null
                : RuntimeProgramTransitionIntent.Cut(
                    new MediaSourceId(current.Routing.ProgramSourceId.Value),
                    new MediaSourceId(command.SourceId.Value));
            return Apply(ControlDomainEngine.Apply(_specification, current, command), command.Metadata.CommandId.Value, transition);
        }
    }

    public ControlHostOperationResult DissolveProgram(DissolveProgramCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            var current = Current();
            var transition = current.Routing.ProgramSourceId == command.SourceId
                ? null
                : RuntimeProgramTransitionIntent.Dissolve(
                    new MediaSourceId(current.Routing.ProgramSourceId.Value),
                    new MediaSourceId(command.SourceId.Value),
                    command.DurationFrames);
            return Apply(ControlDomainEngine.Apply(_specification, current, command), command.Metadata.CommandId.Value, transition);
        }
    }

    public void RecordRuntimeCommit(RuntimeCommitResult result, Identity? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            var revision = _authoritative?.Revision ?? Revision.Initial;
            Journal(
                revision,
                "runtime",
                result.Status == RuntimeCommitStatus.Committed ? "runtime.commit.observed" : "runtime.commit.rejected",
                result.Status == RuntimeCommitStatus.Committed
                    ? $"Runtime execution revision {result.ExecutionRevision} committed."
                    : result.Failure?.Message ?? "Runtime commit rejected.",
                causationId,
                result.Failure);
        }
    }

    public void RecordObservation(string category, string code, string detail, Failure? failure = null)
    {
        lock (_gate)
            Journal(_authoritative?.Revision ?? Revision.Initial, category, code, detail, null, failure);
    }

    private ControlHostOperationResult Apply(
        ControlCommandResult domainResult,
        Identity commandId,
        RuntimeProgramTransitionIntent? transition)
    {
        var current = Current();
        if (!domainResult.Committed)
        {
            var failure = FromValidation("control.command.rejected", domainResult.Validation);
            Journal(current.Revision, "control", "control.command.rejected", failure.Message, commandId, failure);
            return ControlHostOperationResult.Rejected(current, failure);
        }

        var proposed = domainResult.AuthoritativeState;
        var planning = CapabilityPlanningEngine.Plan(_specification, proposed, _providers);
        if (!planning.Succeeded)
        {
            var failure = FromValidation("control.command.planning_rejected", planning.Validation);
            Journal(current.Revision, "planning", "planning.rejected", failure.Message, commandId, failure);
            return ControlHostOperationResult.Rejected(current, failure);
        }

        // The authoritative state advances only after the replacement is completely plan-admissible.
        _authoritative = proposed;
        var execution = Package(proposed, planning, transition);
        Journal(proposed.Revision, "control", "control.command.accepted", "Authoritative production command committed.", commandId, null);
        Journal(proposed.Revision, "planning", "planning.prepared", execution.PreparedExecution.PreparedExecutionId.ToString(), commandId, null);
        return ControlHostOperationResult.Success(execution);
    }

    private static ControlHostExecutionPackage Package(
        AuthoritativeProductionState state,
        ExecutionPlanningResult planning,
        RuntimeProgramTransitionIntent? transition)
    {
        var graph = planning.Graph ?? throw new InvalidOperationException("Successful planning must expose a graph.");
        var programSink = graph.Nodes.Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink).MediaSinkId
            ?? throw new InvalidOperationException("Program sink node must expose a media sink identity.");

        return new ControlHostExecutionPackage(
            state,
            graph,
            planning.PreparedExecution ?? throw new InvalidOperationException("Successful planning must expose a prepared execution."),
            programSink,
            transition);
    }

    private AuthoritativeProductionState Current() =>
        _authoritative ?? throw new InvalidOperationException("ControlHost is not initialized.");

    private void Journal(
        Revision revision,
        string category,
        string code,
        string detail,
        Identity? causationId,
        Failure? failure)
    {
        _journal.TryAppend(new ProductionJournalEvent(
            _specification.ProductionId.Value,
            revision,
            _clock.GetUtcNow(),
            category,
            code,
            detail,
            causationId,
            failure));
    }

    private static Failure FromValidation(string code, ControlValidationReport validation)
    {
        var message = validation.Issues.Count == 0
            ? "Control validation failed without a diagnostic issue."
            : string.Join("; ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        return new Failure(code, message);
    }

    private sealed class SnapshotProviderRegistry : IProviderCapabilityRegistry
    {
        private readonly ProviderDescriptor[] _providers;

        public SnapshotProviderRegistry(IReadOnlyList<ProviderDescriptor> providers)
        {
            if (providers.Any(provider => provider is null))
                throw new ArgumentException("Provider snapshot must not contain null values.", nameof(providers));
            _providers = providers.ToArray();
        }

        public IReadOnlyList<ProviderDescriptor> GetProviders() => Array.AsReadOnly(_providers);
    }
}
